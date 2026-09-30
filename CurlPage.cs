using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed class CurlPage : ToolPage
{
    private readonly TextBox _in, _out;
    private readonly ComboBox _lang;

    public CurlPage() : base("curl", "cURL → Code", "Paste a cURL command (e.g. “Copy as cURL” from the browser dev tools) and get ready-to-run code.")
    {
        _lang = Combo("C# (HttpClient)", "JavaScript (fetch)", "Python (requests)", "PowerShell (Invoke-RestMethod)");
        _lang.MinWidth = 260;
        _in = Input("in", "cURL command", 260, "curl -X POST https://api.example.com/items -H 'Content-Type: application/json' -d '{\"name\":\"test\"}'");
        _out = Output("Generated code", 260);

        _in.TextChanged += (_, _) => Convert();
        _lang.SelectionChanged += (_, _) => Convert();

        Body.Children.Add(Row(Label("Language:"), _lang));
        Body.Children.Add(TwoCols(_in, _out));
        Body.Children.Add(CopyBtn("Copy code", () => _out.Text));
    }

    protected override void OnClipboardInput(string text) => _in.Text = text;

    private void Convert()
    {
        var text = _in.Text ?? "";
        if (text.Trim().Length == 0) { _out.Text = ""; Status.Text = ""; return; }
        try
        {
            var req = CurlTools.Parse(text);
            _out.Text = Sel(_lang) switch
            {
                "JavaScript (fetch)" => CurlTools.ToJavaScript(req),
                "Python (requests)" => CurlTools.ToPython(req),
                "PowerShell (Invoke-RestMethod)" => CurlTools.ToPowerShell(req),
                _ => CurlTools.ToCSharp(req),
            };
            Status.Text = req.Notes.Count > 0 ? "⚠ " + string.Join(" ", req.Notes) : $"{req.Method} {req.Url}";
        }
        catch (Exception ex)
        {
            _out.Text = "";
            Status.Text = "⚠ " + ex.Message;
        }
    }
}
