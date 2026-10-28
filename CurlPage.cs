using Microsoft.UI.Xaml.Controls;

namespace ASD;

public sealed class CurlPage : ToolPage
{
    private readonly TextBox _in, _out;
    private readonly ComboBox _lang;
    private readonly CheckBox _real;
    private bool _realShown;   // the generated code currently contains real key values

    public CurlPage() : base("curl", "cURL → Code", "Paste a cURL command (e.g. “Copy as cURL” from the browser dev tools) and get ready-to-run code.")
    {
        _lang = Combo("C# (HttpClient)", "JavaScript (fetch)", "Python (requests)", "PowerShell (Invoke-RestMethod)");
        _lang.MinWidth = 260;
        _in = Input("in", "cURL command", 260, "curl -X POST https://api.example.com/items -H 'Content-Type: application/json' -d '{\"name\":\"test\"}'");
        _out = Output("Generated code", 260);

        _real = Check("Insert the real key values into the code (asks for the master password)");
        _real.Checked += async (_, _) => await OnRealCheckedAsync();
        _real.Unchecked += (_, _) => Convert();

        // when the vault locks, take the secrets out of the screen again
        void OnLocked() => DispatcherQueue.TryEnqueue(() => { if (_realShown) _real.IsChecked = false; });
        KeyVault.Locked += OnLocked;
        Unloaded += (_, _) => KeyVault.Locked -= OnLocked;

        _in.TextChanged += (_, _) => Convert();
        _lang.SelectionChanged += (_, _) => Convert();

        Body.Children.Add(Row(Label("Language:"), _lang));
        Body.Children.Add(KeyRefs.InsertButton(() => _in));
        Body.Children.Add(KeyRefs.HintText());
        Body.Children.Add(_real);
        Body.Children.Add(TwoCols(_in, _out));
        Body.Children.Add(CopyBtn("Copy code", () => _out.Text));
    }

    protected override void OnClipboardInput(string text) => _in.Text = text;

    private async Task OnRealCheckedAsync()
    {
        if (KeyRefs.Has(_in.Text))
        {
            if (!KeyVault.Exists)
            {
                Status.Text = "⚠ There is no Keys Store yet. Create it from the Keys Store page first.";
                _real.IsChecked = false;
                return;
            }
            var names = KeyRefs.Names(_in.Text);
            var why = "The code will contain the real value of " + string.Join(", ", names) + ". Unlock the Keys Store to continue.";
            if (!await KeyUnlock.EnsureAsync(this, why)) { _real.IsChecked = false; return; }
        }
        Convert();
    }

    private void Convert()
    {
        _realShown = false;
        var text = _in.Text ?? "";
        if (text.Trim().Length == 0) { _out.Text = ""; Status.Text = ""; return; }

        var warning = "";
        if (Is(_real) && KeyRefs.Has(text))
        {
            if (KeyVault.IsUnlocked)
            {
                var set = KeyRefs.Resolve(KeyRefs.Names(text));
                text = KeyRefs.Replace(text, set);
                _realShown = set.Values.Count > 0;
                warning = set.Missing.Count > 0 ? "No key named: " + string.Join(", ", set.Missing) + ". "
                        : "The code contains real key values: do not share it. ";
            }
            else warning = "The Keys Store is locked: untick the box and tick it again to unlock it. ";
        }

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
            var summary = req.Notes.Count > 0 ? string.Join(" ", req.Notes)
                        : _realShown ? req.Method + " (key values inserted)" : $"{req.Method} {req.Url}";
            Status.Text = (warning.Length > 0 || req.Notes.Count > 0 ? "⚠ " : "") + warning + summary;
        }
        catch (Exception ex)
        {
            _out.Text = "";
            _realShown = false;
            Status.Text = "⚠ " + ex.Message;
        }
    }
}
