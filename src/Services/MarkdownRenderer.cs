using Ganss.Xss;
using Markdig;

namespace OpenForge.Services;

public sealed class MarkdownRenderer
{
    private static readonly string[] AllowedTags =
    [
        "p", "br", "strong", "em", "a", "ul", "ol", "li", "h1", "h2", "h3", "h4",
        "blockquote", "code", "pre", "hr", "table", "thead", "tbody", "tr", "th", "td",
    ];

    private readonly MarkdownPipeline _pipeline;
    private readonly HtmlSanitizer _sanitizer;

    public MarkdownRenderer()
    {
        _pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

        _sanitizer = new HtmlSanitizer();
        _sanitizer.AllowedTags.Clear();
        _sanitizer.AllowedAttributes.Clear();
        _sanitizer.AllowedCssProperties.Clear();
        _sanitizer.AllowedAtRules.Clear();
        _sanitizer.AllowedSchemes.Clear();
        _sanitizer.AllowedClasses.Clear();
        _sanitizer.UriAttributes.Clear();
        _sanitizer.AllowDataAttributes = false;
        _sanitizer.KeepChildNodes = true;

        foreach (string tag in AllowedTags)
            _sanitizer.AllowedTags.Add(tag);

        _sanitizer.AllowedAttributes.Add("href");
        _sanitizer.AllowedAttributes.Add("title");
        _sanitizer.UriAttributes.Add("href");
        _sanitizer.AllowedSchemes.Add("http");
        _sanitizer.AllowedSchemes.Add("https");
        _sanitizer.PostProcessNode += (_, e) =>
        {
            if (e.Node is AngleSharp.Dom.IElement element && element.TagName != "A")
            {
                element.RemoveAttribute("href");
                element.RemoveAttribute("title");
            }
        };
    }

    public string Render(string markdown)
    {
        return _sanitizer.Sanitize(Markdown.ToHtml(markdown, _pipeline)).Trim();
    }
}
