using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed class JsonPage : ToolPage
{
    private readonly TextBox _in, _out, _path;
    private readonly ComboBox _indent;
    private readonly CheckBox _infer;

    public JsonPage() : base("json", "JSON Toolkit", "Format, validate, sort, convert to YAML/CSV and query with JSONPath.")
    {
        _in = Input("in", "Input", 280, "Paste JSON here (or YAML / CSV for the reverse conversions)…");
        _out = Output("Output", 280);
        _indent = Combo("2 spaces", "4 spaces", "Tab");
        _path = Input("path", "", placeholder: "$.store.book[?(@.price < 10)].title", multiline: false);
        _path.Width = 460;
        _infer = Check("Detect numbers/booleans in CSV", true);

        Body.Children.Add(TwoCols(_in, _out));
        Body.Children.Add(Row(
            Btn("Format", () => Guard(DoFormat), true),
            Btn("Minify", () => Guard(() => _out.Text = JsonTools.Minify(ParseIn()))),
            Btn("Validate", () => Guard(() => { ParseIn(); Status.Text = "Valid JSON ✔"; })),
            Btn("Sort keys", () => Guard(() => { _out.Text = JsonTools.Format(JsonTools.SortKeys(ParseIn()), Indent); Status.Text = "Keys sorted ✔"; })),
            Label("Indent:"), _indent));
        Body.Children.Add(Row(
            Label("Convert:"),
            Btn("JSON → YAML", () => Guard(() => _out.Text = JsonTools.ToYaml(ParseIn()))),
            Btn("YAML → JSON", () => Guard(() => _out.Text = JsonTools.Format(JsonTools.FromYaml(_in.Text ?? ""), Indent))),
            Btn("JSON → CSV", () => Guard(() => _out.Text = JsonTools.ToCsv(ParseIn()))),
            Btn("CSV → JSON", () => Guard(() => _out.Text = JsonTools.Format(JsonTools.FromCsv(_in.Text ?? "", Is(_infer)), Indent))),
            _infer));
        Body.Children.Add(Row(
            Btn("Escape string", () => Guard(() => _out.Text = JsonTools.Quote(_in.Text ?? ""))),
            Btn("Unescape string", () => Guard(Unescape))));
        Body.Children.Add(Label("JSONPath query"));
        Body.Children.Add(Row(_path, Btn("Query", () => Guard(DoQuery), true)));
        Body.Children.Add(Row(
            CopyBtn("Copy output", () => _out.Text),
            Btn("Use output as input", () => _in.Text = _out.Text),
            Btn("Clear", () => { _in.Text = ""; _out.Text = ""; Status.Text = ""; })));
    }

    private string Indent => Sel(_indent) switch { "4 spaces" => "    ", "Tab" => "\t", _ => "  " };

    private JsonNode? ParseIn() => JsonTools.Parse(_in.Text ?? "");

    private void Guard(Action a)
    {
        try { a(); }
        catch (Exception ex) { Status.Text = JsonTools.DescribeError(ex); }
    }

    private void DoFormat()
    {
        _out.Text = JsonTools.Format(ParseIn(), Indent);
        Status.Text = "Valid JSON ✔";
    }

    private void Unescape()
    {
        var t = (_in.Text ?? "").Trim();
        if (!t.StartsWith("\"")) t = "\"" + t + "\"";
        _out.Text = JsonSerializer.Deserialize<string>(t) ?? "";
    }

    private void DoQuery()
    {
        var results = JsonTools.Query(ParseIn(), _path.Text ?? "");
        if (results.Count == 1) _out.Text = JsonTools.Format(results[0], Indent);
        else
        {
            var arr = new JsonArray();
            foreach (var r in results) arr.Add(r?.DeepClone());
            _out.Text = JsonTools.Format(arr, Indent);
        }
        Status.Text = $"{results.Count} match{(results.Count == 1 ? "" : "es")}";
    }

    protected override void OnClipboardInput(string text)
    {
        _in.Text = text;
        Guard(DoFormat);
    }
}
