using System.Text.Encodings.Web;
using MatMail.Controls;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;

namespace MatMail.Tests;

/// <summary>The controls (TagHelpers <c>mm-…</c>) render markup of the toolkit: the classes, roles and data attributes the toolkit script and the style sheet rely on.</summary>
public class ControlTests
{
    private static readonly Toolkit Icons = new(new PhysicalFileProvider(Path.Combine(FindRoot(), "src", "MatMail", "wwwroot")));

    private static string FindRoot()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "MatMail.slnx")))
        {
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("The repository root was not found.");
        }

        return root;
    }

    private sealed class Echo : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(name.Replace("{0}", "{0}"), arguments));

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private static async Task<string> RenderAsync(TagHelper helper, string tag, string content = "", params (string Name, object Value)[] attributes)
    {
        var list = new TagHelperAttributeList(attributes.Select(a => new TagHelperAttribute(a.Name, a.Value)));
        var context = new TagHelperContext(tag, list, new Dictionary<object, object>(), Guid.NewGuid().ToString("N"));
        var output = new TagHelperOutput(tag, list, (_, _) => Task.FromResult<TagHelperContent>(new DefaultTagHelperContent().SetHtmlContent(content)));
        await helper.ProcessAsync(context, output);
        if (!output.IsContentModified)
        {
            output.Content.SetHtmlContent(content);   // what the runtime does with the child content of a tag that did not set its own
        }

        using var writer = new StringWriter();
        output.WriteTo(writer, HtmlEncoder.Default);
        return writer.ToString();
    }

    [Fact]
    public async Task A_button_is_a_button_of_the_toolkit_and_a_link_that_looks_like_one_is_an_anchor()
    {
        string button = await RenderAsync(new ButtonTagHelper(Icons) { Variant = "secondary", Icon = "save" }, "mm-button", "Save");
        string link = await RenderAsync(new ButtonTagHelper(Icons) { Variant = "primary", Href = "/Admin" }, "mm-button", "Open");

        Assert.Contains("<button", button);
        Assert.Contains("class=\"mat-btn mat-btn--secondary\"", button);
        Assert.Contains("type=\"submit\"", button);
        Assert.Contains("<use href=\"#mat-i-save\">", button);
        Assert.Contains("<span>Save</span>", button);
        Assert.StartsWith("<a", link);
        Assert.Contains("href=\"/Admin\"", link);
    }

    [Fact]
    public async Task A_button_that_destroys_asks_first_and_posts_to_its_handler()
    {
        string html = await RenderAsync(new ButtonTagHelper(Icons) { Variant = "danger", Handler = "Delete", Confirm = "Delete it?", ConfirmOk = "Delete" }, "mm-button", "Delete");

        Assert.Contains("class=\"mat-btn mat-btn--danger\"", html);
        Assert.Contains("data-confirm=\"Delete it?\"", html);
        Assert.Contains("data-confirm-ok=\"Delete\"", html);
        Assert.Contains("formaction=\"?handler=Delete\"", html);
    }

    [Fact]
    public async Task An_icon_button_has_its_text_as_name_and_tooltip()
    {
        string html = await RenderAsync(new ButtonTagHelper(Icons) { Variant = "ghost", Icon = "edit", IconOnly = true, Small = true, Href = "/x" }, "mm-button", "Edit");

        Assert.Contains("class=\"mat-btn mat-btn--ghost mat-btn--icon mat-btn--sm\"", html);
        Assert.Contains("aria-label=\"Edit\"", html);
        Assert.Contains("data-tip=\"Edit\"", html);
        Assert.DoesNotContain("<span>Edit</span>", html);
    }

    [Theory]
    [InlineData("ok", "mat-pill mat-pill--ok")]
    [InlineData("warn", "mat-pill mat-pill--warn")]
    [InlineData("danger", "mat-pill mat-pill--error")]
    [InlineData("info", "mat-pill mat-pill--info")]
    [InlineData("muted", "mat-pill")]
    public async Task A_status_is_a_pill_with_a_word(string kind, string classes)
        => Assert.Contains($"class=\"{classes}\"", await RenderAsync(new BadgeTagHelper(Icons) { Kind = kind }, "mm-badge", "Active"));

    [Fact]
    public async Task A_badge_with_an_icon_is_a_tag_and_not_a_state()
    {
        string html = await RenderAsync(new BadgeTagHelper(Icons) { Kind = "info", Icon = "users" }, "mm-badge", "Public folder");

        Assert.Contains("class=\"mat-tag\"", html);
        Assert.Contains("#mat-i-users", html);
    }

    [Theory]
    [InlineData("info", "mat-alert--info", "status")]
    [InlineData("warn", "mat-alert--warning", "status")]
    [InlineData("danger", "mat-alert--danger", "alert")]
    [InlineData("ok", "mat-alert--success", "status")]
    public async Task An_alert_has_the_tone_the_icon_and_the_role_of_its_kind(string kind, string tone, string role)
    {
        string html = await RenderAsync(new AlertTagHelper(Icons) { Kind = kind }, "mm-alert", "Text");

        Assert.Contains($"mat-alert {tone}", html);
        Assert.Contains($"role=\"{role}\"", html);
        Assert.Contains("<svg class=\"mat-icon\"", html);
        Assert.Contains("<div class=\"mat-alert__body\">Text</div>", html);
    }

    [Fact]
    public async Task An_alert_can_have_a_title_and_an_icon_of_its_own()
    {
        string html = await RenderAsync(new AlertTagHelper(Icons) { Kind = "info", Icon = "server", Title = "Directory" }, "mm-alert", "Text");

        Assert.Contains("#mat-i-server", html);
        Assert.Contains("<span class=\"mat-alert__title\">Directory</span>", html);
    }

    [Fact]
    public async Task A_card_has_a_head_with_icon_and_title_and_a_body()
    {
        string html = await RenderAsync(new CardTagHelper(Icons) { Title = "Server", Icon = "server", Flush = true }, "mm-card", "<p>x</p>");

        Assert.StartsWith("<section class=\"mat-card mat-card--flush\">", html);
        Assert.Contains("<div class=\"mat-card__head\">", html);
        Assert.Contains("<h2 class=\"mat-card__title\">Server</h2>", html);
        Assert.Contains("<div class=\"mat-card__body mat-stack\"><p>x</p></div>", html);
    }

    [Fact]
    public async Task A_card_keeps_the_classes_the_page_gives_it()
    {
        string html = await RenderAsync(new CardTagHelper(Icons) { Title = "A" }, "mm-card", "x", ("class", "mat-col-12 mat-col-md-6"));

        Assert.Contains("class=\"mat-card mat-col-12 mat-col-md-6\"", html);
    }

    [Theory]
    [InlineData(63, "", null)]
    [InlineData(80, "warn", "warn")]
    [InlineData(95, "high", "error")]
    [InlineData(100, "full", "error")]
    public async Task A_meter_has_its_share_in_data_and_the_tone_of_its_state(int percent, string state, string? tone)
    {
        string html = await RenderAsync(new MeterTagHelper { Percent = percent, State = state, Label = "Storage" }, "mm-meter");

        Assert.Contains("role=\"progressbar\"", html);
        Assert.Contains($"aria-valuenow=\"{percent}\"", html);
        Assert.Contains($"data-value=\"{percent}\"", html);
        Assert.Contains("aria-label=\"Storage\"", html);
        Assert.DoesNotContain("style=", html);
        if (tone is null)
        {
            Assert.DoesNotContain("data-tone", html);
        }
        else
        {
            Assert.Contains($"data-tone=\"{tone}\"", html);
        }
    }

    [Fact]
    public async Task A_meter_stays_between_zero_and_a_hundred()
    {
        Assert.Contains("aria-valuenow=\"100\"", await RenderAsync(new MeterTagHelper { Percent = 140 }, "mm-meter"));
        Assert.Contains("aria-valuenow=\"0\"", await RenderAsync(new MeterTagHelper { Percent = -5 }, "mm-meter"));
    }

    [Fact]
    public async Task A_table_without_rows_shows_the_empty_state_and_with_rows_the_table_with_labels()
    {
        var table = new TableTagHelper(new Echo(), Icons) { ItemCount = 0, EmptyText = "Nothing here.", EmptyIcon = "inbox" };
        string empty = await RenderAsync(table, "mm-table", "<thead></thead>");

        Assert.Contains("class=\"mat-empty\"", empty);
        Assert.Contains("Nothing here.", empty);
        Assert.DoesNotContain("<table", empty);

        string filled = await RenderAsync(new TableTagHelper(new Echo(), Icons) { ItemCount = 1, Caption = "Mailboxes" }, "mm-table",
            "<thead><tr><th>Name</th></tr></thead><tbody><tr><td>A</td></tr></tbody>");

        Assert.Contains("class=\"mat-table-wrap\"", filled);
        Assert.Contains("<table class=\"mat-table\">", filled);
        Assert.Contains("<caption class=\"mat-sr-only\">Mailboxes</caption>", filled);
        Assert.Contains("<td data-label=\"Name\">A</td>", filled);
    }

    [Fact]
    public async Task A_list_is_the_container_the_toolkit_orders()
    {
        string html = await RenderAsync(new ListTagHelper(), "mm-list", "<x/>");

        Assert.Equal("<div class=\"mat-list\"><x/></div>", html);
    }

    [Fact]
    public async Task Pagination_renders_nothing_for_one_page_and_keeps_the_other_query_values()
    {
        var one = new PaginationTagHelper(new Echo(), Icons) { CurrentPage = 1, TotalPages = 1, ViewContext = ViewContextFor("") };
        Assert.Equal(string.Empty, await RenderAsync(one, "mm-pagination"));

        var three = new PaginationTagHelper(new Echo(), Icons) { CurrentPage = 2, TotalPages = 3, TotalCount = 55, PageSize = 20, ViewContext = ViewContextFor("?Search=ann&Sort=name_asc&PageNumber=2") };
        string html = await RenderAsync(three, "mm-pagination");

        Assert.Contains("class=\"mat-pager\"", html);
        Assert.Contains("21–40 of 55", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains("aria-current=\"page\"", html);
        Assert.Contains("Search=ann", html);
        Assert.Contains("Sort=name_asc", html);
        Assert.Contains("PageNumber=3", html);
        foreach (System.Text.RegularExpressions.Match link in System.Text.RegularExpressions.Regex.Matches(html, "<a [^>]*?href=\"([^\"]*)\""))
        {
            Assert.True(System.Text.RegularExpressions.Regex.Matches(link.Groups[1].Value, "PageNumber=").Count == 1, link.Value);   // the page of the request is replaced, not added
        }
    }

    private static ViewContext ViewContextFor(string query)
    {
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString(query);
        return new ViewContext { HttpContext = http };
    }
}
