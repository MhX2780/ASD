using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed class FormatPage : ToolPage
{
    private readonly TextBox _in, _out;
    private readonly ComboBox _mode, _indent;

    public FormatPage() : base("format", "Formatters", "Beautify or minify XML, HTML and SQL.")
    {
        _mode = Combo("XML", "HTML", "SQL");
        _indent = Combo("2 spaces", "4 spaces");
        _indent.MinWidth = 120;
        _in = Input("in", "Input", 300, "Paste XML, HTML or SQL here…");
        _out = Output("Output", 300);

        Body.Children.Add(Row(Label("Language:"), _mode, Label("Indent:"), _indent));
        Body.Children.Add(TwoCols(_in, _out));
        Body.Children.Add(Row(
            Btn("Format", () => Run(false), true),
            Btn("Minify", () => Run(true)),
            CopyBtn("Copy output", () => _out.Text),
            Btn("Use output as input", () => _in.Text = _out.Text)));
    }

    private void Run(bool minify)
    {
        var text = _in.Text ?? "";
        int indent = Sel(_indent) == "4 spaces" ? 4 : 2;
        try
        {
            _out.Text = (Sel(_mode), minify) switch
            {
                ("XML", false) => FormatTools.Xml(text, indent),
                ("XML", true) => FormatTools.XmlMinify(text),
                ("HTML", false) => FormatTools.Html(text, indent),
                ("HTML", true) => FormatTools.HtmlMinify(text),
                (_, false) => FormatTools.Sql(text, indent),
                _ => FormatTools.SqlMinify(text),
            };
            Status.Text = "Done ✔";
        }
        catch (System.Xml.XmlException ex) { Status.Text = "Invalid XML: " + ex.Message; }
    }
}
