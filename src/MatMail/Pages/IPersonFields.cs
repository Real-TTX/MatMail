namespace MatMail.Pages;

/// <summary>The personal details of a user that more than one form edits (see <c>Shared/_PersonFields.cshtml</c>).</summary>
public interface IPersonFields
{
    string? Salutation { get; set; }
    string? Title { get; set; }
    string? FirstName { get; set; }
    string? LastName { get; set; }
    string? Department { get; set; }
    string? Phone { get; set; }
    string? Mobile { get; set; }
    string? Fax { get; set; }
}
