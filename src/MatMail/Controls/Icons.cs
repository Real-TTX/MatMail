using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace MatMail.Controls;

/// <summary>
/// The icon set (24×24, stroke based, Lucide/Feather style). Every icon is defined once here and emitted as an SVG sprite
/// by the layout; markup and scripts reference icons with <c>&lt;use href="#i-name"&gt;</c>.
/// </summary>
public static class Icons
{
    private static readonly Dictionary<string, string> Shapes = new(StringComparer.Ordinal)
    {
        ["menu"] = "<path d='M4 12h16M4 6h16M4 18h16'/>",
        ["x"] = "<path d='M18 6 6 18M6 6l12 12'/>",
        ["check"] = "<path d='M20 6 9 17l-5-5'/>",
        ["plus"] = "<path d='M5 12h14M12 5v14'/>",
        ["minus"] = "<path d='M5 12h14'/>",
        ["search"] = "<circle cx='11' cy='11' r='8'/><path d='m21 21-4.3-4.3'/>",
        ["chevron-down"] = "<path d='m6 9 6 6 6-6'/>",
        ["chevron-up"] = "<path d='m18 15-6-6-6 6'/>",
        ["chevron-left"] = "<path d='m15 18-6-6 6-6'/>",
        ["chevron-right"] = "<path d='m9 18 6-6-6-6'/>",
        ["chevrons-left"] = "<path d='m11 17-5-5 5-5M18 17l-5-5 5-5'/>",
        ["chevrons-right"] = "<path d='m6 17 5-5-5-5M13 17l5-5-5-5'/>",
        ["arrow-left"] = "<path d='m12 19-7-7 7-7M19 12H5'/>",
        ["arrow-right"] = "<path d='M5 12h14m-7-7 7 7-7 7'/>",
        ["arrow-up"] = "<path d='m5 12 7-7 7 7M12 19V5'/>",
        ["arrow-down"] = "<path d='M12 5v14m7-7-7 7-7-7'/>",
        ["mail"] = "<rect width='20' height='16' x='2' y='4' rx='2'/><path d='m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7'/>",
        ["mail-open"] = "<path d='M21.2 8.4c.5.38.8.97.8 1.6v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V10a2 2 0 0 1 .8-1.6l8-6a2 2 0 0 1 2.4 0l8 6Z'/><path d='m22 10-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 10'/>",
        ["mail-plus"] = "<path d='M22 13V6a2 2 0 0 0-2-2H4a2 2 0 0 0-2 2v12c0 1.1.9 2 2 2h8'/><path d='m22 7-8.97 5.7a1.94 1.94 0 0 1-2.06 0L2 7'/><path d='M19 16v6M16 19h6'/>",
        ["inbox"] = "<path d='M22 12h-6l-2 3h-4l-2-3H2'/><path d='M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z'/>",
        ["send"] = "<path d='m22 2-7 20-4-9-9-4Z'/><path d='M22 2 11 13'/>",
        ["file"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z'/><path d='M14 2v4a2 2 0 0 0 2 2h4'/>",
        ["file-text"] = "<path d='M15 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V7Z'/><path d='M14 2v4a2 2 0 0 0 2 2h4M10 9H8M16 13H8M16 17H8'/>",
        ["trash"] = "<path d='M3 6h18'/><path d='M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6'/><path d='M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2'/><path d='M10 11v6M14 11v6'/>",
        ["archive"] = "<rect width='20' height='5' x='2' y='3' rx='1'/><path d='M4 8v11a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8'/><path d='M10 12h4'/>",
        ["star"] = "<path d='M12 2l3.09 6.26L22 9.27l-5 4.87 1.18 6.88L12 17.77l-6.18 3.25L7 14.14 2 9.27l6.91-1.01L12 2z'/>",
        ["spam"] = "<path d='M7.86 2h8.28L22 7.86v8.28L16.14 22H7.86L2 16.14V7.86L7.86 2z'/><path d='M12 8v4M12 16h.01'/>",
        ["folder"] = "<path d='M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z'/>",
        ["folder-plus"] = "<path d='M20 20a2 2 0 0 0 2-2V8a2 2 0 0 0-2-2h-7.9a2 2 0 0 1-1.69-.9L9.6 3.9A2 2 0 0 0 7.93 3H4a2 2 0 0 0-2 2v13a2 2 0 0 0 2 2Z'/><path d='M12 10v6M9 13h6'/>",
        ["folder-input"] = "<path d='M2 9V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H20a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2h-1'/><path d='M2 13h10'/><path d='m9 16 3-3-3-3'/>",
        ["tag"] = "<path d='M12.586 2.586A2 2 0 0 0 11.172 2H4a2 2 0 0 0-2 2v7.172a2 2 0 0 0 .586 1.414l8.704 8.704a2.426 2.426 0 0 0 3.42 0l6.58-6.58a2.426 2.426 0 0 0 0-3.42z'/><circle cx='7.5' cy='7.5' r='.5' fill='currentColor'/>",
        ["users"] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M22 21v-2a4 4 0 0 0-3-3.87M16 3.13a4 4 0 0 1 0 7.75'/>",
        ["user"] = "<path d='M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2'/><circle cx='12' cy='7' r='4'/>",
        ["user-plus"] = "<path d='M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2'/><circle cx='9' cy='7' r='4'/><path d='M19 8v6M22 11h-6'/>",
        ["shield"] = "<path d='M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z'/>",
        ["key"] = "<path d='m21 2-2 2m-7.61 7.61a5.5 5.5 0 1 1-7.778 7.778 5.5 5.5 0 0 1 7.777-7.777zm0 0L15.5 7.5m0 0 3 3L22 7l-3-3m-3.5 3.5L19 4'/>",
        ["lock"] = "<rect width='18' height='11' x='3' y='11' rx='2' ry='2'/><path d='M7 11V7a5 5 0 0 1 10 0v4'/>",
        ["globe"] = "<circle cx='12' cy='12' r='10'/><path d='M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20'/><path d='M2 12h20'/>",
        ["server"] = "<rect width='20' height='8' x='2' y='2' rx='2' ry='2'/><rect width='20' height='8' x='2' y='14' rx='2' ry='2'/><path d='M6 6h.01M6 18h.01'/>",
        ["settings"] = "<circle cx='12' cy='12' r='3'/><path d='M19.4 15a1.65 1.65 0 0 0 .33 1.82l.06.06a2 2 0 1 1-2.83 2.83l-.06-.06a1.65 1.65 0 0 0-1.82-.33 1.65 1.65 0 0 0-1 1.51V21a2 2 0 0 1-4 0v-.09A1.65 1.65 0 0 0 9 19.4a1.65 1.65 0 0 0-1.82.33l-.06.06a2 2 0 1 1-2.83-2.83l.06-.06a1.65 1.65 0 0 0 .33-1.82 1.65 1.65 0 0 0-1.51-1H3a2 2 0 0 1 0-4h.09A1.65 1.65 0 0 0 4.6 9a1.65 1.65 0 0 0-.33-1.82l-.06-.06a2 2 0 1 1 2.83-2.83l.06.06a1.65 1.65 0 0 0 1.82.33H9a1.65 1.65 0 0 0 1-1.51V3a2 2 0 0 1 4 0v.09a1.65 1.65 0 0 0 1 1.51 1.65 1.65 0 0 0 1.82-.33l.06-.06a2 2 0 1 1 2.83 2.83l-.06.06a1.65 1.65 0 0 0-.33 1.82V9a1.65 1.65 0 0 0 1.51 1H21a2 2 0 0 1 0 4h-.09a1.65 1.65 0 0 0-1.51 1z'/>",
        ["building"] = "<rect width='16' height='20' x='4' y='2' rx='2' ry='2'/><path d='M9 22v-4h6v4M8 6h.01M16 6h.01M12 6h.01M12 10h.01M12 14h.01M16 10h.01M16 14h.01M8 10h.01M8 14h.01'/>",
        ["at-sign"] = "<circle cx='12' cy='12' r='4'/><path d='M16 8v5a3 3 0 0 0 6 0v-1a10 10 0 1 0-4 8'/>",
        ["link"] = "<path d='M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71'/><path d='M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71'/>",
        ["plug"] = "<path d='M12 22v-5M9 8V2M15 8V2'/><path d='M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z'/>",
        ["refresh"] = "<path d='M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8'/><path d='M21 3v5h-5'/><path d='M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16'/><path d='M8 16H3v5'/>",
        ["filter"] = "<path d='M22 3H2l8 9.46V19l4 2v-8.54L22 3z'/>",
        ["sliders"] = "<path d='M4 21v-7M4 10V3M12 21v-9M12 8V3M20 21v-5M20 12V3M2 14h4M10 8h4M18 16h4'/>",
        ["edit"] = "<path d='M12 20h9'/><path d='M16.5 3.5a2.121 2.121 0 0 1 3 3L7 19l-4 1 1-4 12.5-12.5z'/>",
        ["copy"] = "<rect width='14' height='14' x='8' y='8' rx='2' ry='2'/><path d='M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2'/>",
        ["download"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><path d='m7 10 5 5 5-5M12 15V3'/>",
        ["upload"] = "<path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'/><path d='m17 8-5-5-5 5M12 3v12'/>",
        ["paperclip"] = "<path d='m21.44 11.05-9.19 9.19a6 6 0 0 1-8.49-8.49l8.57-8.57A4 4 0 1 1 18 8.84l-8.59 8.57a2 2 0 0 1-2.83-2.83l8.49-8.48'/>",
        ["reply"] = "<path d='m9 17-5-5 5-5'/><path d='M20 18v-2a4 4 0 0 0-4-4H4'/>",
        ["reply-all"] = "<path d='m7 17-5-5 5-5M12 17l-5-5 5-5'/><path d='M22 18v-2a4 4 0 0 0-4-4H7'/>",
        ["forward"] = "<path d='m15 17 5-5-5-5'/><path d='M4 18v-2a4 4 0 0 1 4-4h12'/>",
        ["more-vertical"] = "<circle cx='12' cy='12' r='1'/><circle cx='12' cy='5' r='1'/><circle cx='12' cy='19' r='1'/>",
        ["more-horizontal"] = "<circle cx='12' cy='12' r='1'/><circle cx='19' cy='12' r='1'/><circle cx='5' cy='12' r='1'/>",
        ["log-out"] = "<path d='M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4'/><path d='m16 17 5-5-5-5M21 12H9'/>",
        ["moon"] = "<path d='M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z'/>",
        ["sun"] = "<circle cx='12' cy='12' r='4'/><path d='M12 2v2M12 20v2m-7.07-15.07 1.41 1.41m11.32 11.32 1.41 1.41M2 12h2m16 0h2M6.34 17.66l-1.41 1.41M19.07 4.93l-1.41 1.41'/>",
        ["monitor"] = "<rect width='20' height='14' x='2' y='3' rx='2'/><path d='M8 21h8M12 17v4'/>",
        ["eye"] = "<path d='M2 12s3-7 10-7 10 7 10 7-3 7-10 7-10-7-10-7Z'/><circle cx='12' cy='12' r='3'/>",
        ["eye-off"] = "<path d='M9.88 9.88a3 3 0 1 0 4.24 4.24'/><path d='M10.73 5.08A10.43 10.43 0 0 1 12 5c7 0 10 7 10 7a13.16 13.16 0 0 1-1.67 2.68'/><path d='M6.61 6.61A13.526 13.526 0 0 0 2 12s3 7 10 7a9.74 9.74 0 0 0 5.39-1.61'/><path d='m2 2 20 20'/>",
        ["clock"] = "<circle cx='12' cy='12' r='10'/><path d='M12 6v6l4 2'/>",
        ["calendar"] = "<rect width='18' height='18' x='3' y='4' rx='2' ry='2'/><path d='M16 2v4M8 2v4M3 10h18'/>",
        ["activity"] = "<path d='M22 12h-4l-3 9L9 3l-3 9H2'/>",
        ["list"] = "<path d='M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01'/>",
        ["list-ordered"] = "<path d='M10 6h11M10 12h11M10 18h11'/><path d='M4 6h1v4M4 10h2'/><path d='M6 18H4c0-1 2-2 2-3s-1-1.5-2-1'/>",
        ["dashboard"] = "<rect x='3' y='3' width='7' height='9' rx='1'/><rect x='14' y='3' width='7' height='5' rx='1'/><rect x='14' y='12' width='7' height='9' rx='1'/><rect x='3' y='16' width='7' height='5' rx='1'/>",
        ["database"] = "<ellipse cx='12' cy='5' rx='9' ry='3'/><path d='M3 5v14a9 3 0 0 0 18 0V5'/><path d='M3 12a9 3 0 0 0 18 0'/>",
        ["network"] = "<rect x='16' y='16' width='6' height='6' rx='1'/><rect x='2' y='16' width='6' height='6' rx='1'/><rect x='9' y='2' width='6' height='6' rx='1'/><path d='M5 16v-3a1 1 0 0 1 1-1h12a1 1 0 0 1 1 1v3M12 12V8'/>",
        ["pen-line"] = "<path d='M12 20h9'/><path d='M16.376 3.622a1 1 0 0 1 3.002 3.002L7.368 18.635a2 2 0 0 1-.855.506l-2.872.838a.5.5 0 0 1-.62-.62l.838-2.872a2 2 0 0 1 .506-.854z'/>",
        ["external-link"] = "<path d='M15 3h6v6M10 14 21 3'/><path d='M18 13v6a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h6'/>",
        ["help"] = "<circle cx='12' cy='12' r='10'/><path d='M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3M12 17h.01'/>",
        ["bell"] = "<path d='M6 8a6 6 0 0 1 12 0c0 7 3 9 3 9H3s3-2 3-9'/><path d='M10.3 21a1.94 1.94 0 0 0 3.4 0'/>",
        ["printer"] = "<path d='M6 9V2h12v7'/><path d='M6 18H4a2 2 0 0 1-2-2v-5a2 2 0 0 1 2-2h16a2 2 0 0 1 2 2v5a2 2 0 0 1-2 2h-2'/><rect width='12' height='8' x='6' y='14'/>",
        ["bold"] = "<path d='M6 12h9a4 4 0 0 1 0 8H7a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1h7a4 4 0 0 1 0 8'/>",
        ["italic"] = "<path d='M19 4h-9M14 20H5M15 4 9 20'/>",
        ["underline"] = "<path d='M6 4v6a6 6 0 0 0 12 0V4M4 20h16'/>",
        ["image"] = "<rect width='18' height='18' x='3' y='3' rx='2' ry='2'/><circle cx='9' cy='9' r='2'/><path d='m21 15-3.086-3.086a2 2 0 0 0-2.828 0L6 21'/>",
        ["info"] = "<circle cx='12' cy='12' r='10'/><path d='M12 16v-4M12 8h.01'/>",
        ["alert"] = "<path d='m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3'/><path d='M12 9v4M12 17h.01'/>",
        ["alert-circle"] = "<circle cx='12' cy='12' r='10'/><path d='M12 8v4M12 16h.01'/>",
        ["check-circle"] = "<path d='M22 11.08V12a10 10 0 1 1-5.93-9.14'/><path d='m9 11 3 3L22 4'/>",
        ["x-circle"] = "<circle cx='12' cy='12' r='10'/><path d='m15 9-6 6M9 9l6 6'/>",
        ["play"] = "<path d='M6 3l14 9-14 9V3z'/>",
        ["rotate-ccw"] = "<path d='M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8'/><path d='M3 3v5h5'/>",
        ["home"] = "<path d='m3 9 9-7 9 7v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z'/><path d='M9 22V12h6v10'/>",
        ["hard-drive"] = "<path d='M22 12H2'/><path d='M5.45 5.11 2 12v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.55-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z'/><path d='M6 16h.01M10 16h.01'/>",
        ["maximize"] = "<path d='M15 3h6v6M9 21H3v-6M21 3l-7 7M3 21l7-7'/>",
        ["save"] = "<path d='M19 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h11l5 5v11a2 2 0 0 1-2 2z'/><path d='M17 21v-8H7v8M7 3v5h8'/>",
        ["type"] = "<path d='M4 7V4h16v3M9 20h6M12 4v16'/>",
        ["shuffle"] = "<path d='M2 18h1.4c1.3 0 2.5-.6 3.3-1.7l6.1-8.6c.7-1.1 2-1.7 3.3-1.7H22'/><path d='m18 2 4 4-4 4'/><path d='M2 6h1.9c1.5 0 2.9.9 3.6 2.2'/><path d='M22 18h-5.9c-1.3 0-2.6-.7-3.3-1.8l-.5-.8'/><path d='m18 14 4 4-4 4'/>",
        ["clipboard"] = "<rect width='8' height='4' x='8' y='2' rx='1' ry='1'/><path d='M16 4h2a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2'/>",
        ["terminal"] = "<path d='m4 17 6-6-6-6M12 19h8'/>",
        ["zap"] = "<path d='M13 2 3 14h9l-1 8 10-12h-9l1-8z'/>",
        ["package"] = "<path d='m7.5 4.27 9 5.15'/><path d='M21 8a2 2 0 0 0-1-1.73l-7-4a2 2 0 0 0-2 0l-7 4A2 2 0 0 0 3 8v8a2 2 0 0 0 1 1.73l7 4a2 2 0 0 0 2 0l7-4A2 2 0 0 0 21 16Z'/><path d='m3.3 7 8.7 5 8.7-5M12 22V12'/>",
        ["undo"] = "<path d='M3 7v6h6'/><path d='M21 17a9 9 0 0 0-9-9 9 9 0 0 0-6 2.3L3 13'/>",
        ["toggle-left"] = "<rect width='20' height='12' x='2' y='6' rx='6' ry='6'/><circle cx='8' cy='12' r='2'/>",
        ["smartphone"] = "<rect width='14' height='20' x='5' y='2' rx='2' ry='2'/><path d='M12 18h.01'/>",
    };

    public static bool Exists(string name) => Shapes.ContainsKey(name);

    private static readonly Lazy<string> SpriteMarkup = new(BuildSprite);

    /// <summary>The SVG sprite with one <c>&lt;symbol id="i-name"&gt;</c> per icon. Emitted once per page by the layout.</summary>
    public static string Sprite() => SpriteMarkup.Value;

    private static string BuildSprite()
    {
        var sb = new StringBuilder(Shapes.Count * 220);
        sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" style=\"display:none\" aria-hidden=\"true\">");
        foreach ((string name, string shape) in Shapes)
        {
            sb.Append("<symbol id=\"i-").Append(name).Append("\" viewBox=\"0 0 24 24\">").Append(shape).Append("</symbol>");
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    /// <summary>Markup for one icon, usable from C# (e.g. in strings that go through Html.Raw).</summary>
    public static string Markup(string name, string? cssClass = null)
    {
        string classes = string.IsNullOrEmpty(cssClass) ? "icon" : "icon " + cssClass;
        return $"<svg class=\"{HtmlEncoder.Default.Encode(classes)}\" aria-hidden=\"true\"><use href=\"#i-{HtmlEncoder.Default.Encode(name)}\"/></svg>";
    }
}

/// <summary>
/// An icon from the sprite. Usage: <c>&lt;mm-icon name="mail" /&gt;</c>; <c>filled="true"</c> fills the shape (the star);
/// <c>size</c> is a CSS size such as "1.25rem".
/// </summary>
[HtmlTargetElement("mm-icon", TagStructure = TagStructure.WithoutEndTag)]
public sealed class IconTagHelper : TagHelper
{
    [HtmlAttributeName("name")]
    public string Name { get; set; } = string.Empty;

    [HtmlAttributeName("filled")]
    public bool Filled { get; set; }

    [HtmlAttributeName("size")]
    public string? Size { get; set; }

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = "svg";
        output.TagMode = TagMode.StartTagAndEndTag;

        string cssClass = "icon";
        if (Filled)
        {
            cssClass += " icon--filled";
        }

        if (output.Attributes.TryGetAttribute("class", out TagHelperAttribute? extra))
        {
            cssClass += " " + extra.Value;
        }

        output.Attributes.SetAttribute("class", cssClass);
        output.Attributes.SetAttribute("aria-hidden", "true");
        if (!string.IsNullOrEmpty(Size))
        {
            output.Attributes.SetAttribute("style", $"width:{Size};height:{Size}");
        }

        output.Content.SetHtmlContent($"<use href=\"#i-{HtmlEncoder.Default.Encode(Name)}\"/>");
    }
}
