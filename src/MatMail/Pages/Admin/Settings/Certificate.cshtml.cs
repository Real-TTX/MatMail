using System.Security.Cryptography.X509Certificates;
using MatMail.Data;
using MatMail.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Localization;

namespace MatMail.Pages.Admin.Settings;

public class CertificateModel(CertificateProvider certificates, ActivityLogger log, CurrentUser currentUser, IStringLocalizer<SharedResource> l) : PageModel
{
    public CertificateInfo? Info => certificates.Describe();

    public void OnGet()
    {
    }

    /// <summary>Installs an uploaded PEM chain + key, or a PKCS#12 file, after checking that it works.</summary>
    public async Task<IActionResult> OnPostUploadAsync(IFormFile? certificateFile, IFormFile? keyFile, string? pfxPassword)
    {
        if (certificateFile is null || certificateFile.Length == 0)
        {
            this.NotifyNow(l["Choose a certificate file."].Value, NoticeKind.Danger);
            return Page();
        }

        string directory = certificates.CertificateDirectory;
        string temp = Path.Combine(directory, "upload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            string certPath = Path.Combine(temp, "certificate");
            await using (FileStream stream = System.IO.File.Create(certPath))
            {
                await certificateFile.CopyToAsync(stream);
            }

            byte[] bytes = await System.IO.File.ReadAllBytesAsync(certPath);
            bool isPem = System.Text.Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 64)).Contains("-----BEGIN", StringComparison.Ordinal);

            if (isPem)
            {
                if (keyFile is null || keyFile.Length == 0)
                {
                    this.NotifyNow(l["A PEM certificate needs its private key file."].Value, NoticeKind.Danger);
                    return Page();
                }

                string keyPath = Path.Combine(temp, "key");
                await using (FileStream stream = System.IO.File.Create(keyPath))
                {
                    await keyFile.CopyToAsync(stream);
                }

                using X509Certificate2 check = X509Certificate2.CreateFromPemFile(certPath, keyPath);
                if (!Usable(check, out string? problem))
                {
                    this.NotifyNow(problem, NoticeKind.Danger);
                    return Page();
                }

                System.IO.File.Copy(certPath, Path.Combine(directory, "fullchain.pem"), overwrite: true);
                System.IO.File.Copy(keyPath, Path.Combine(directory, "privkey.pem"), overwrite: true);
                System.IO.File.Delete(Path.Combine(directory, "server.pfx"));
            }
            else
            {
                using X509Certificate2 check = X509CertificateLoader.LoadPkcs12(bytes, string.IsNullOrEmpty(pfxPassword) ? null : pfxPassword);
                if (!Usable(check, out string? problem))
                {
                    this.NotifyNow(problem, NoticeKind.Danger);
                    return Page();
                }

                System.IO.File.Copy(certPath, Path.Combine(directory, "server.pfx"), overwrite: true);
                if (!string.IsNullOrEmpty(pfxPassword))
                {
                    this.NotifyNow(l["PKCS#12 files with a password: set it as MATMAIL__Tls__PfxPassword so it can be read after a restart."].Value, NoticeKind.Warn);
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or IOException or ArgumentException)
        {
            this.NotifyNow(string.Format(l["The certificate could not be read: {0}"].Value, ex.Message), NoticeKind.Danger);
            return Page();
        }
        finally
        {
            try
            {
                Directory.Delete(temp, recursive: true);
            }
            catch (IOException)
            {
                // Removed with the next upload.
            }
        }

        certificates.Reload();
        await log.InfoAsync(ActivityCategory.Admin, "A TLS certificate was installed.", userId: currentUser.UserId);
        this.Notify(l["The certificate is installed and in use."].Value);
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUseSelfSignedAsync()
    {
        string directory = certificates.CertificateDirectory;
        foreach (string file in new[] { "server.pfx", "fullchain.pem", "privkey.pem" })
        {
            System.IO.File.Delete(Path.Combine(directory, file));
        }

        certificates.Reload();
        await log.InfoAsync(ActivityCategory.Admin, "The TLS certificate was reset to a self-signed one.", userId: currentUser.UserId);
        this.Notify(l["A self-signed certificate is used again."].Value);
        return RedirectToPage();
    }

    private bool Usable(X509Certificate2 certificate, out string? problem)
    {
        problem = null;
        if (!certificate.HasPrivateKey)
        {
            problem = l["The file contains no private key."].Value;
        }
        else if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow)
        {
            problem = l["The certificate has expired."].Value;
        }

        return problem is null;
    }
}
