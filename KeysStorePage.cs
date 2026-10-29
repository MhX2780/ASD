using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

/// <summary>
/// Encrypted vault for API keys.
///   Header : "Keys Store" on the left, [Add new key] [Search] [Security] on the right.
///   Body   : only the key cards (an empty-state hint when there are none).
///   Click a card → unlock dialog (Windows Hello / Password / Cancel) → content hides → progress ring →
///   a dialog with the key's information and value: Close (main button) and Edit.
/// Names, services and tags stay visible/searchable while locked; values are AES-256-GCM encrypted.
/// Once unlocked the vault stays open until the idle timeout or "Lock now" (Security).
/// </summary>
public sealed class KeysStorePage : ToolPage
{
    private const string AllTags = "All tags";
    private const string AllServices = "All services";

    private static readonly string[] Icons =
        { "\uE72E", "\uE753", "\uE774", "\uE715", "\uE716", "\uE71B", "\uE756", "\uE719", "\uE713" };
    private static readonly string[] IconNames =
        { "Key / vault", "Cloud", "Website", "Email", "Account", "Link", "Developer", "Payments", "Settings" };

    // the two mutually-exclusive views
    private readonly StackPanel _listView = new() { Spacing = 12 };
    private readonly StackPanel _busyView = new()
    {
        Spacing = 16, Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 70, 0, 70),
    };
    private readonly TextBlock _busyText = new() { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 };

    private readonly StackPanel _cardsHost = new() { Spacing = 8 };

    private ContentDialog? _infoDialog;
    private bool _flow;          // a dialog / auth flow is running
    private bool _helloSupported;
    private int _copyToken;

    public KeysStorePage() : base("keys", "Keys Store", "")
    {
        // ── header: title on one side, the buttons on the opposite side ──
        var title = new TextBlock { Text = "Keys Store", FontSize = 26, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
        var add = IconBtn("\uE710", "Add new key", () => Run(AddAsync), accent: true);
        var searchBtn = IconBtn("\uE721", "Search", () => Run(SearchAsync));
        var gear = Btn("", () => Run(SecurityAsync));
        gear.Content = new FontIcon { Glyph = "\uE713", FontSize = 16 };
        AutomationProperties.SetName(gear, "Security");
        ToolTipService.SetToolTip(gear, "Security");

        var header = new Grid();
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
            Children = { add, searchBtn, gear },
        };
        Grid.SetColumn(actions, 1);
        header.Children.Add(title);
        header.Children.Add(actions);
        ((StackPanel)((ScrollViewer)Content).Content).Children[0] = header;   // replace the base class's title + subtitle block

        _busyView.Children.Add(new ProgressRing { IsActive = true, Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center });
        _busyView.Children.Add(_busyText);

        _listView.Children.Add(_cardsHost);
        Body.Children.Add(_listView);
        Body.Children.Add(_busyView);

        Loaded += async (_, _) =>
        {
            KeyVault.Locked += OnVaultLocked;
            try
            {
                _helloSupported = await KeyVault.IsHelloSupportedAsync();
                RebuildCards();
            }
            catch (Exception ex) { Fail(ex); }
        };
        Unloaded += (_, _) => KeyVault.Locked -= OnVaultLocked;

        try { RebuildCards(); }
        catch (Exception ex) { Fail(ex); }
    }

    private Button IconBtn(string glyph, string text, Action onClick, bool accent = false)
    {
        var b = Btn(text, onClick, accent);
        b.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { new FontIcon { Glyph = glyph, FontSize = 14 }, new TextBlock { Text = text } },
        };
        AutomationProperties.SetName(b, text);
        return b;
    }

    /// <summary>A dialog that follows the app's theme setting (Light / Dark), not the Windows theme.</summary>
    private ContentDialog NewDialog() => new() { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };

    // ───────────────────────── views ─────────────────────────
    private void ShowList()
    {
        _busyView.Visibility = Visibility.Collapsed;
        _listView.Visibility = Visibility.Visible;
    }

    private void ShowBusy(string text)
    {
        _busyText.Text = text;
        _listView.Visibility = Visibility.Collapsed;
        _busyView.Visibility = Visibility.Visible;
    }

    private void OnVaultLocked() => DispatcherQueue.TryEnqueue(() =>
    {
        _infoDialog?.Hide();     // never leave a decrypted value on screen after locking
        RebuildCards();
        Status.Text = "Vault locked.";
    });

    // ───────────────────────── search dialog ─────────────────────────
    private static IEnumerable<KeyEntry> Filter(List<KeyEntry> all, string text, string tag, string service, string state, bool inValues)
    {
        var terms = (text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var today = DateTime.Today;

        foreach (var e in all)
        {
            if (tag.Length > 0 && tag != AllTags && !e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            if (service.Length > 0 && service != AllServices && !string.Equals(e.Service, service, StringComparison.OrdinalIgnoreCase)) continue;

            var days = e.Expires is DateTime x ? (x.Date - today).Days : int.MaxValue;
            var ok = state switch
            {
                "Favorites" => e.Favorite,
                "Expired" => e.Expires != null && days < 0,
                "Expiring in 30 days" => e.Expires != null && days >= 0 && days <= 30,
                "Has expiry" => e.Expires != null,
                "No expiry" => e.Expires == null,
                _ => true,
            };
            if (!ok) continue;

            if (terms.Length > 0)
            {
                var hay = $"{e.Name} {KeyRefs.RefOf(e)} {e.Service} {string.Join(' ', e.Tags)} {e.Notes}";
                if (inValues) hay += " " + (KeyVault.TryReveal(e.Id) ?? "");
                hay = hay.ToLowerInvariant();
                if (!terms.All(hay.Contains)) continue;
            }
            yield return e;
        }
    }

    /// <summary>
    /// Search + filters in a dialog. The results are wide horizontal cards; picking one closes this dialog and
    /// starts the normal "open a key" dialog (a ContentDialog cannot open another one on top of itself).
    /// </summary>
    private async Task SearchAsync()
    {
        Status.Text = "";
        var all = KeyVault.All();
        if (all.Count == 0) { Status.Text = "There are no keys to search yet."; return; }

        var box = new TextBox { PlaceholderText = "Search name, service, tags, notes …", IsSpellCheckEnabled = false };
        AutomationProperties.SetName(box, "Search keys");

        var tag = Combo(AllTags);
        var service = Combo(AllServices);
        var state = Combo("All keys", "Favorites", "Expired", "Expiring in 30 days", "Has expiry", "No expiry");
        foreach (var t in all.SelectMany(k => k.Tags).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
            tag.Items.Add(t);
        foreach (var v in all.Select(k => k.Service).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            service.Items.Add(v);

        var inValues = Check("Also search inside values");
        inValues.IsEnabled = KeyVault.IsUnlocked;       // values can only be searched while the vault is open
        var count = new TextBlock { Opacity = 0.7, FontSize = 12 };
        var results = new StackPanel { Spacing = 6 };

        var dlg = NewDialog();
        string? picked = null;
        var shown = new List<KeyEntry>();

        void Refresh()
        {
            shown = Filter(all, box.Text ?? "", Sel(tag), Sel(service), Sel(state), Is(inValues) && KeyVault.IsUnlocked)
                .OrderByDescending(k => k.Favorite).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
            count.Text = $"{shown.Count} of {all.Count} keys";
            results.Children.Clear();
            if (shown.Count == 0)
                results.Children.Add(new TextBlock { Text = "Nothing matches your search or filters.", Opacity = 0.7, Margin = new Thickness(4, 8, 0, 0) });
            foreach (var k in shown)
            {
                var id = k.Id;
                results.Children.Add(WideCard(k, () => { picked = id; dlg.Hide(); }));
            }
        }

        box.TextChanged += (_, _) => Refresh();
        foreach (var c in new[] { tag, service, state }) c.SelectionChanged += (_, _) => Refresh();
        inValues.Checked += (_, _) => Refresh();
        inValues.Unchecked += (_, _) => Refresh();
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Windows.System.VirtualKey.Enter && shown.Count > 0) { picked = shown[0].Id; dlg.Hide(); }   // Enter opens the first result
        };
        Refresh();

        dlg.Title = "Search keys";
        dlg.CloseButtonText = "Close";
        dlg.DefaultButton = ContentDialogButton.Close;
        dlg.Content = new StackPanel
        {
            Spacing = 10, Width = 620,
            Children = { box, Row(tag, service, state), inValues, count, new ScrollViewer { MaxHeight = 340, Content = results } },
        };
        dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        await dlg.ShowAsync();
        if (picked != null) await RevealAsync(picked);
    }

    /// <summary>A wide, horizontal result card: icon · name + details · favorite star / expiry.</summary>
    private Button WideCard(KeyEntry e, Action onPick)
    {
        var meta = string.Join("  ·  ", new[] { e.Service }.Concat(e.Tags).Where(t => t.Length > 0));
        var (expiryText, level) = ExpiryInfo(e);

        var grid = new Grid { ColumnSpacing = 14 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = IconView(e.Icon, 24);
        var text = new StackPanel
        {
            Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = e.Name, FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = meta.Length > 0 ? meta : " ", Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis },
            },
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        if (expiryText != null)
        {
            var t = new TextBlock { Text = expiryText, FontSize = 12 };
            if (level == 2) t.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            else if (level == 1) t.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
            else t.Opacity = 0.6;
            right.Children.Add(t);
        }
        if (e.Favorite) right.Children.Add(new FontIcon { Glyph = "\uE735", FontSize = 14 });

        Grid.SetColumn(text, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        grid.Children.Add(right);

        var b = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 10, 14, 10),
        };
        AutomationProperties.SetName(b, "Result " + e.Name);     // distinct from the card on the page behind the dialog
        b.Click += (_, _) => onPick();
        return b;
    }

    /// <summary>The top of every password / unlock dialog: a round icon, a heading and a short hint, centred.</summary>
    private static UIElement AuthHeader(string heading, string hint)
    {
        var circle = new Border
        {
            Width = 72, Height = 72, CornerRadius = new CornerRadius(36), HorizontalAlignment = HorizontalAlignment.Center,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 128, 128, 128)),
            Child = new FontIcon { Glyph = "\uE72E", FontSize = 32, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        return new StackPanel
        {
            Spacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6),
            Children =
            {
                circle,
                new TextBlock { Text = heading, FontSize = 18, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0) },
                new TextBlock { Text = hint, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center },
            },
        };
    }

    private void RebuildCards()
    {
        try
        {
            var all = KeyVault.All();
            var shown = all.OrderByDescending(e => e.Favorite).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

            _cardsHost.Children.Clear();

            if (all.Count == 0)
            {
                // empty state: the same icon as the navigation bar, centred, with a hint
                _cardsHost.Children.Add(new StackPanel
                {
                    Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 90, 0, 0),
                    Children =
                    {
                        new FontIcon { Glyph = "\uE72E", FontSize = 72, Opacity = 0.45, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = "Click Add Key above to add new key", Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center, TextWrapping = TextWrapping.Wrap },
                    },
                });
            }
            else if (shown.Count == 0)
            {
                _cardsHost.Children.Add(new TextBlock { Text = "Nothing matches your search or filters.", Opacity = 0.7 });
            }
            else
            {
                _cardsHost.Children.Add(new ItemsRepeater
                {
                    ItemsSource = shown.Select(Card).ToList(),
                    Layout = new UniformGridLayout
                    {
                        MinItemWidth = 280, MinItemHeight = 124, MinRowSpacing = 6, MinColumnSpacing = 6,
                        ItemsStretch = UniformGridLayoutItemsStretch.Fill,
                    },
                });
            }
            if (IsLoaded) CursorHelper.ApplyHandCursorToButtons(this);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private static string IconOf(KeyEntry e) => string.IsNullOrEmpty(e.Icon) ? Icons[0] : e.Icon;

    private Brush IconBrush() => new SolidColorBrush(ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(255, 240, 240, 240) : Windows.UI.Color.FromArgb(255, 32, 32, 32));

    /// <summary>A key's icon: a Segoe glyph (built-in) or a Simple Icons logo ("si:github"), drawn in the theme's text colour.</summary>
    private FrameworkElement IconView(string? icon, double size, HorizontalAlignment h = HorizontalAlignment.Center, Thickness? margin = null)
    {
        FrameworkElement? el = null;
        var brand = icon != null && icon.StartsWith(SimpleIcons.Prefix);
        if (brand) el = SimpleIcons.Create(icon![SimpleIcons.Prefix.Length..], size, IconBrush());
        el ??= new FontIcon { Glyph = string.IsNullOrEmpty(icon) || brand ? Icons[0] : icon, FontSize = size };
        el.HorizontalAlignment = h;
        el.VerticalAlignment = VerticalAlignment.Center;
        if (margin is Thickness m) el.Margin = m;
        return el;
    }

    private static string IconLabel(string key)
    {
        if (key.StartsWith(SimpleIcons.Prefix)) return SimpleIcons.Find(key[SimpleIcons.Prefix.Length..])?.Title ?? key[SimpleIcons.Prefix.Length..];
        var i = Array.IndexOf(Icons, key);
        return i >= 0 ? IconNames[i] : "Icon";
    }

    private UIElement Card(KeyEntry e)
    {
        var meta = string.Join("  ·  ", new[] { e.Service }.Concat(e.Tags).Where(s => s.Length > 0));
        var (expiryText, level) = ExpiryInfo(e);

        var content = new StackPanel
        {
            Spacing = 3, Margin = new Thickness(0, 0, 92, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                IconView(e.Icon, 22, HorizontalAlignment.Left, new Thickness(0, 0, 0, 4)),
                new TextBlock { Text = e.Name, FontWeight = FontWeights.Bold, FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Left },
                new TextBlock { Text = meta.Length > 0 ? meta : " ", Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Left },
            },
        };
        if (expiryText != null)
        {
            var t = new TextBlock { Text = expiryText, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left };
            if (level == 2) t.Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed);
            else if (level == 1) t.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Orange);
            else t.Opacity = 0.6;
            content.Children.Add(t);
        }

        var main = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, VerticalContentAlignment = VerticalAlignment.Top,
            Padding = new Thickness(14), Margin = new Thickness(4),
        };
        AutomationProperties.SetName(main, e.Name);
        main.Click += (_, _) => Run(() => RevealAsync(e.Id));

        var star = SmallIconButton(e.Favorite ? "\uE735" : "\uE734", e.Favorite ? "Remove from favorites" : "Add to favorites");
        star.Click += (_, _) => { KeyVault.SetFavorite(e.Id, !e.Favorite); RebuildCards(); };
        var edit = SmallIconButton("\uE70F", "Edit or rename");
        edit.Click += (_, _) => Run(() => EditAsync(e.Id));

        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            Children = { edit, star },
        };
        if (KeyVault.IsUnlocked)    // quick copy straight from the card while the vault is open
        {
            var copy = SmallIconButton("\uE8C8", "Copy key");
            copy.Click += (_, _) =>
            {
                try { CopySecret(KeyVault.Reveal(e.Id)); }
                catch (Exception ex) { Fail(ex); }
            };
            corner.Children.Insert(0, copy);
        }

        var g = new Grid { Height = 124 };
        g.Children.Add(main);
        g.Children.Add(corner);
        return g;
    }

    private static Button SmallIconButton(string glyph, string tip)
    {
        var b = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 14 },
            Padding = new Thickness(6),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(b, tip);
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    /// <summary>level: 0 = fine, 1 = expires soon, 2 = expired.</summary>
    private static (string? Text, int Level) ExpiryInfo(KeyEntry e)
    {
        if (e.Expires is not DateTime exp) return (null, 0);
        var days = (exp.Date - DateTime.Today).Days;
        if (days < 0) return ($"Expired {-days} day{(days == -1 ? "" : "s")} ago", 2);
        if (days == 0) return ("Expires today", 1);
        if (days <= 30) return ($"Expires in {days} day{(days == 1 ? "" : "s")}", 1);
        return ($"Expires {exp:yyyy-MM-dd}", 0);
    }

    // ───────────────────────── flows ─────────────────────────
    /// <summary>Runs an async flow once at a time, restoring the list and showing the error if it throws.</summary>
    private async void Run(Func<Task> work)
    {
        if (_flow) return;
        _flow = true;
        try { await work(); }
        catch (Exception ex) { ShowList(); Fail(ex); }
        finally
        {
            _flow = false;
            try { RebuildCards(); } catch { /* page may be gone */ }
        }
    }

    private enum Stage { Auth, Busy, Info }

    private async Task RevealAsync(string id)
    {
        var entry = KeyVault.Find(id);
        if (entry == null) return;
        Status.Text = "";

        var askEvery = SettingsStore.Get("keys.askEvery", "off") == "on";
        var needAuth = !KeyVault.IsUnlocked || askEvery;
        var helloReady = needAuth && KeyVault.HelloEnabled && await KeyVault.IsHelloSupportedAsync();

        if (await OpenKeyDialogAsync(entry, needAuth, helloReady)) await EditAsync(id);
    }

    private UIElement KeyTitle(KeyEntry e) => new StackPanel
    {
        Orientation = Orientation.Horizontal, Spacing = 12,
        Children =
        {
            IconView(e.Icon, 24, HorizontalAlignment.Left),
            new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 },
        },
    };

    /// <summary>Fades an element's opacity (the final value is held, so the next fade starts from it).</summary>
    private static Task FadeAsync(UIElement el, double from, double to, int ms = 180)
    {
        var done = new TaskCompletionSource();
        var anim = new DoubleAnimation
        {
            From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            FillBehavior = FillBehavior.HoldEnd,
        };
        Storyboard.SetTarget(anim, el);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Completed += (_, _) => done.TrySetResult();
        sb.Begin();
        return done.Task;
    }

    private async void Safe(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex) { Fail(ex); }
    }

    /// <summary>
    /// ONE dialog for the whole "open a key" trip. Its content is swapped with a fade:
    ///   password / Windows Hello  →  progress ring in the middle  →  the key's information.
    /// The buttons follow the stage (Windows Hello · Password · Cancel, then Close · Edit).
    /// Returns true when the user pressed Edit.
    /// </summary>
    private async Task<bool> OpenKeyDialogAsync(KeyEntry e, bool needAuth, bool helloReady)
    {
        var stage = needAuth ? Stage.Auth : Stage.Busy;
        var wantEdit = false;
        var closed = false;
        TextBox? valueBox = null;

        // layer 1: master password / Windows Hello
        var pw = new PasswordBox { PlaceholderText = "Master password", MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
        var err = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
        };
        var authView = new StackPanel
        {
            Spacing = 10, VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                AuthHeader("Unlock to open this key", helloReady ? "Use Windows Hello, or type your master password." : "Type your master password."),
                pw, err,
            },
        };

        // layer 2: progress ring, centred in the dialog
        var busyText = new TextBlock { Text = "Decrypting …", HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 };
        var busyView = new StackPanel
        {
            Spacing = 14, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { new ProgressRing { IsActive = true, Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center }, busyText },
        };

        // layer 3: the key itself (filled once it is decrypted)
        var infoView = new StackPanel { Spacing = 10 };

        authView.Visibility = needAuth ? Visibility.Visible : Visibility.Collapsed;
        busyView.Visibility = needAuth ? Visibility.Collapsed : Visibility.Visible;
        infoView.Visibility = Visibility.Collapsed;
        FrameworkElement current = needAuth ? authView : busyView;

        var dlg = NewDialog();
        dlg.Title = KeyTitle(e);
        dlg.Content = new ScrollViewer
        {
            MaxHeight = 480,
            Content = new Grid { Width = 460, MinHeight = 230, Children = { authView, busyView, infoView } },
        };

        void Buttons(string? primary, string? secondary, string? close, ContentDialogButton def)
        {
            dlg.PrimaryButtonText = primary ?? "";
            dlg.SecondaryButtonText = secondary ?? "";
            dlg.CloseButtonText = close ?? "";
            dlg.DefaultButton = def;
        }
        void AuthButtons() => Buttons(helloReady ? "Windows Hello" : null, "Unlock", "Cancel",
                                       helloReady ? ContentDialogButton.Primary : ContentDialogButton.Secondary);

        async Task GoToAsync(FrameworkElement next)
        {
            if (closed || ReferenceEquals(current, next)) return;
            var from = current;
            current = next;
            await FadeAsync(from, 1, 0);
            from.Visibility = Visibility.Collapsed;
            next.Opacity = 0;
            next.Visibility = Visibility.Visible;
            await FadeAsync(next, 0, 1);
        }

        async Task ShowInfoAsync()
        {
            string secret;
            try { secret = KeyVault.Reveal(e.Id); }
            catch (Exception ex)
            {
                Status.Text = $"Error: {ex.Message}";
                dlg.Hide();
                return;
            }

            var meta = string.Join("  ·  ", new[] { e.Service }.Concat(e.Tags).Where(t => t.Length > 0));
            valueBox = new TextBox
            {
                Header = "Value", Text = secret, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"), MinHeight = 84, MaxHeight = 220,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(valueBox, ScrollBarVisibility.Auto);

            var box = valueBox;
            var shown = true;
            Button? toggle = null;
            toggle = Btn("Hide", () =>
            {
                shown = !shown;
                box.Text = shown ? secret : new string('•', Math.Clamp(secret.Length, 8, 32));
                toggle!.Content = shown ? "Hide" : "Show";
            });
            var copy = Btn("Copy", () => { });
            copy.Click += (_, _) => { CopySecret(secret); copy.Content = "Copied"; ResetLabel(copy, "Copy"); };

            var lines = new List<string>();
            if (e.Expires is DateTime exp) lines.Add($"Expires {exp:yyyy-MM-dd}");
            lines.Add($"Added {e.Created:yyyy-MM-dd HH:mm}");
            lines.Add($"Changed {e.Modified:yyyy-MM-dd HH:mm}");

            infoView.Children.Clear();
            if (meta.Length > 0) infoView.Children.Add(new TextBlock { Text = meta, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
            infoView.Children.Add(box);
            infoView.Children.Add(Row(copy, toggle));
            infoView.Children.Add(new TextBlock
            {
                Text = "In other tools (HTTP Track, cURL): " + KeyRefs.Token(e),
                Opacity = 0.75, FontSize = 12, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true,
            });
            if (e.Notes.Length > 0)
                infoView.Children.Add(new TextBlock { Text = e.Notes, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
            infoView.Children.Add(new TextBlock { Text = string.Join("   ·   ", lines), Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            infoView.Children.Add(new TextBlock { Text = "The clipboard is cleared 30 seconds after you copy.", Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap });

            await GoToAsync(infoView);
            if (closed) return;
            _infoDialog = dlg;                      // so an idle lock can close it
            stage = Stage.Info;
            Buttons("Close", "Edit", null, ContentDialogButton.Primary);
        }

        async Task UnlockAsync(bool hello)
        {
            var typed = pw.Password;
            if (!hello && typed.Length == 0)
            {
                err.Text = "Type your master password.";
                err.Visibility = Visibility.Visible;
                return;
            }

            stage = Stage.Busy;
            Buttons(null, null, null, ContentDialogButton.None);
            busyText.Text = hello ? "Waiting for Windows Hello …" : "Unlocking …";
            await GoToAsync(busyView);

            var ok = hello ? await KeyVault.UnlockWithHelloAsync()
                           : await Task.Run(() => KeyVault.UnlockWithPassword(typed));
            if (closed) return;

            if (!ok)
            {
                err.Text = hello ? "Windows Hello did not unlock the vault. Use your master password." : "Wrong password.";
                err.Visibility = Visibility.Visible;
                pw.Password = "";
                stage = Stage.Auth;
                AuthButtons();
                await GoToAsync(authView);
                pw.Focus(FocusState.Programmatic);
                return;
            }
            busyText.Text = "Decrypting …";
            await ShowInfoAsync();
        }

        if (needAuth) AuthButtons();
        else Buttons(null, null, null, ContentDialogButton.None);

        dlg.PrimaryButtonClick += (_, a) =>
        {
            if (stage == Stage.Info) return;                    // "Close": let the dialog close
            a.Cancel = true;                                    // "Windows Hello": stay open and swap the content
            if (stage == Stage.Auth) Safe(() => UnlockAsync(true));
        };
        dlg.SecondaryButtonClick += (_, a) =>
        {
            if (stage == Stage.Info) { wantEdit = true; return; }   // "Edit": close, then the edit dialog opens
            a.Cancel = true;                                        // "Unlock"
            if (stage == Stage.Auth) Safe(() => UnlockAsync(false));
        };
        pw.KeyDown += (_, k) =>
        {
            if (k.Key == Windows.System.VirtualKey.Enter && stage == Stage.Auth) Safe(() => UnlockAsync(false));
        };
        dlg.Opened += (_, _) =>
        {
            if (needAuth) { if (!helloReady) pw.Focus(FocusState.Programmatic); }
            else Safe(async () => { await Task.Delay(450); await ShowInfoAsync(); });   // already unlocked: ring, then the key
        };
        dlg.Closed += (_, _) =>
        {
            closed = true;
            _infoDialog = null;
            if (valueBox != null) valueBox.Text = "";            // drop the decrypted value
        };

        await dlg.ShowAsync();
        return wantEdit;
    }

    private static async void ResetLabel(Button b, string text)
    {
        await Task.Delay(1500);
        b.Content = text;
    }

    private async Task AddAsync()
    {
        Status.Text = "";
        if (!KeyVault.IsUnlocked)
        {
            if (!await AuthenticateAsync("Unlock to add a key")) { ShowList(); return; }
            ShowList();
        }
        var result = await EditDialogAsync(null);
        if (result == null) return;
        KeyVault.Upsert(result.Entry, result.Secret);
        Status.Text = $"Saved “{result.Entry.Name}”.";
    }

    private async Task EditAsync(string id)
    {
        var existing = KeyVault.Find(id);
        if (existing == null) return;
        Status.Text = "";

        var result = await EditDialogAsync(existing);
        if (result == null) return;

        if (result.Delete)
        {
            var confirm = NewDialog();
            confirm.Title = $"Delete “{existing.Name}”?";
            confirm.Content = "The key and its encrypted value are removed from this PC. This cannot be undone.";
            confirm.PrimaryButtonText = "Delete";
            confirm.CloseButtonText = "Cancel";
            confirm.DefaultButton = ContentDialogButton.Close;
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            KeyVault.Delete(id);
            Status.Text = $"Deleted “{existing.Name}”.";
            return;
        }

        if (!string.IsNullOrEmpty(result.Secret) && !KeyVault.IsUnlocked)
        {
            if (!await AuthenticateAsync("Unlock to change the value")) { ShowList(); Status.Text = "Not saved."; return; }
            ShowList();
        }
        KeyVault.Upsert(result.Entry, result.Secret);
        Status.Text = $"Saved “{result.Entry.Name}”.";
    }

    private sealed record EditResult(KeyEntry Entry, string? Secret, bool Delete);

    /// <summary>What the Add / Edit dialog holds, kept while the "More icons" dialog is open (only one dialog can be open at a time).</summary>
    private sealed class Draft
    {
        public string Name = "", Ref = "", Service = "", Tags = "", Value = "", Notes = "", Icon = "";
        public bool RefTouched;   // false = the string value still follows the name while typing
        public DateTimeOffset? Expires;
        public bool Favorite;
    }

    /// <summary>
    /// "Icon" drop-down: the nine built-in icons, the currently chosen logo (if any) and "More icons".
    /// Choosing "More icons" asks the caller to open the full picker.
    /// </summary>
    private ComboBox IconCombo(string current, Action onMore, Action<string> onPick)
    {
        var combo = new ComboBox { Header = "Icon", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(combo, "Icon picker");

        ComboBoxItem Item(string key, string label, FrameworkElement icon)
        {
            var item = new ComboBoxItem
            {
                Tag = key,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 10,
                    Children = { icon, new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center } },
                },
            };
            AutomationProperties.SetName(item, label);
            return item;
        }

        for (var i = 0; i < Icons.Length; i++) combo.Items.Add(Item(Icons[i], IconNames[i], IconView(Icons[i], 18)));
        if (!Icons.Contains(current)) combo.Items.Add(Item(current, IconLabel(current), IconView(current, 18)));   // a logo chosen earlier
        combo.Items.Add(Item("more", "More icons", new FontIcon { Glyph = "\uE712", FontSize = 18 }));

        foreach (var it in combo.Items.OfType<ComboBoxItem>())
            if (it.Tag as string == current) { combo.SelectedItem = it; break; }

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            if (tag == "more") onMore(); else onPick(tag);
        };
        return combo;
    }

    private sealed record IconDef(string Key, string Title, string Hay);

    private sealed class IconFactory : IElementFactory
    {
        private readonly Func<IconDef, UIElement> _make;
        public IconFactory(Func<IconDef, UIElement> make) => _make = make;
        public UIElement GetElement(ElementFactoryGetArgs args) => _make((IconDef)args.Data);
        public void RecycleElement(ElementFactoryRecycleArgs args) { }
    }

    /// <summary>The full icon picker: the built-in icons plus ~3 500 brand logos (Simple Icons), with search. Returns the chosen key or null.</summary>
    private async Task<string?> PickIconAsync(string current)
    {
        var defs = new List<IconDef>();
        for (var i = 0; i < Icons.Length; i++) defs.Add(new IconDef(Icons[i], IconNames[i], ("built-in " + IconNames[i]).ToLowerInvariant()));
        foreach (var br in SimpleIcons.All) defs.Add(new IconDef(SimpleIcons.Prefix + br.Slug, br.Title, (br.Title + " " + br.Slug).ToLowerInvariant()));

        var box = new TextBox { PlaceholderText = "Search icons — github, gemini, docker …", IsSpellCheckEnabled = false };
        AutomationProperties.SetName(box, "Search icons");
        var count = new TextBlock { Opacity = 0.7, FontSize = 12 };

        var dlg = NewDialog();
        string? picked = null;
        var shown = defs;

        UIElement Make(IconDef d)
        {
            var b = new Button
            {
                Content = IconView(d.Key, 28), Width = 64, Height = 64, Padding = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            if (d.Key == current) b.BorderThickness = new Thickness(2);
            AutomationProperties.SetName(b, "Icon " + d.Title);
            ToolTipService.SetToolTip(b, d.Title);
            b.Click += (_, _) => { picked = d.Key; dlg.Hide(); };
            return b;
        }

        var repeater = new ItemsRepeater
        {
            ItemTemplate = new IconFactory(Make),
            Layout = new UniformGridLayout { MinItemWidth = 64, MinItemHeight = 64, MinRowSpacing = 6, MinColumnSpacing = 6 },
        };

        void Refresh()
        {
            var terms = (box.Text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            shown = terms.Length == 0 ? defs : defs.Where(d => terms.All(d.Hay.Contains)).ToList();
            repeater.ItemsSource = shown;
            count.Text = $"{shown.Count:N0} icons";
        }
        box.TextChanged += (_, _) => Refresh();
        box.KeyDown += (_, k) =>
        {
            if (k.Key == Windows.System.VirtualKey.Enter && shown.Count > 0) { picked = shown[0].Key; dlg.Hide(); }
        };
        Refresh();

        dlg.Title = "More icons";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Close;
        dlg.Content = new StackPanel
        {
            Spacing = 10, Width = 620,
            Children =
            {
                box, count,
                new ScrollViewer { Height = 380, Content = repeater },
                new TextBlock
                {
                    Text = "Brand logos: Simple Icons (CC0). The logos are trademarks of their owners.",
                    Opacity = 0.55, FontSize = 11, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);

        await dlg.ShowAsync();
        return picked;
    }

    private async Task<EditResult?> EditDialogAsync(KeyEntry? existing)
    {
        var isNew = existing == null;
        var draft = new Draft
        {
            Name = existing?.Name ?? "", Service = existing?.Service ?? "",
            Ref = existing == null ? "" : KeyRefs.RefOf(existing), RefTouched = existing != null,   // an existing key keeps its reference when renamed
            Tags = existing == null ? "" : string.Join(", ", existing.Tags),
            Notes = existing?.Notes ?? "", Favorite = existing?.Favorite ?? false,
            Icon = existing == null ? Icons[0] : IconOf(existing),
            Expires = existing?.Expires is DateTime d ? new DateTimeOffset(d.Date) : null,
        };

        while (true)   // loops once more after the "More icons" dialog, with everything typed so far kept in the draft
        {
            var name = new TextBox { Header = "Name", PlaceholderText = "e.g. OpenAI – production", Text = draft.Name };
            var refBox = new TextBox { Header = "String value (optional, no spaces)", PlaceholderText = "e.g. Azure-Key", Text = draft.Ref, IsSpellCheckEnabled = false };
            AutomationProperties.SetName(refBox, "String value");
            var refHint = new TextBlock { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            var settingRef = false;
            void UpdateHint()
            {
                var r = refBox.Text.Trim();
                if (r.Length == 0) r = KeyRefs.Slug(name.Text);
                refHint.Text = "Other tools use it as {{key:" + (r.Length > 0 ? r : "…") + "}}";
            }
            name.TextChanged += (_, _) =>
            {
                if (!draft.RefTouched) { settingRef = true; refBox.Text = KeyRefs.Slug(name.Text); settingRef = false; }
                UpdateHint();
            };
            refBox.TextChanged += (_, _) => { if (!settingRef) draft.RefTouched = true; UpdateHint(); };
            UpdateHint();
            var service = new TextBox { Header = "Service", PlaceholderText = "e.g. OpenAI, GitHub, Stripe", Text = draft.Service };
            var tags = new TextBox { Header = "Tags (comma separated)", PlaceholderText = "prod, billing", Text = draft.Tags };
            var value = new TextBox
            {
                Header = isNew ? "Value" : "New value (leave empty to keep the current one)",
                AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 84, IsSpellCheckEnabled = false,
                FontFamily = new FontFamily("Consolas"), Text = draft.Value,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(value, ScrollBarVisibility.Auto);
            var notes = new TextBox { Header = "Notes", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, Text = draft.Notes };
            var expires = new CalendarDatePicker { Header = "Expires (optional)", PlaceholderText = "No expiry" };
            if (draft.Expires != null) expires.Date = draft.Expires;
            var fav = new CheckBox { Content = "Favorite", IsChecked = draft.Favorite };
            var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

            var more = false;
            var dlg = NewDialog();
            var icon = IconCombo(draft.Icon, () => { more = true; dlg.Hide(); }, v => draft.Icon = v);

            dlg.Title = isNew ? "Add key" : "Edit key";
            dlg.PrimaryButtonText = "Save";
            dlg.CloseButtonText = "Cancel";
            dlg.DefaultButton = ContentDialogButton.Primary;
            dlg.Content = new ScrollViewer
            {
                MaxHeight = 540,
                Content = new StackPanel { Spacing = 10, Width = 460, Children = { icon, name, refBox, refHint, service, tags, value, notes, expires, fav, error } },
            };
            if (!isNew) dlg.SecondaryButtonText = "Delete";

            dlg.PrimaryButtonClick += (_, a) =>
            {
                string? problem = null;
                var reference = refBox.Text.Trim();
                if (reference.Length == 0) reference = KeyRefs.Slug(name.Text);
                if (name.Text.Trim().Length == 0) problem = "Give the key a name.";
                else if (reference.Length == 0) problem = "Type a string value with letters or digits: other tools use it as {{key:…}}.";
                else if (!KeyRefs.IsValidRef(reference)) problem = "The string value cannot have spaces or symbols. Use letters, digits, - _ .";
                else if (KeyVault.All().Any(k => k.Id != existing?.Id && string.Equals(KeyRefs.RefOf(k), reference, StringComparison.OrdinalIgnoreCase)))
                    problem = "Another key already uses {{key:" + reference + "}}. Pick a different string value.";
                else if (isNew && value.Text.Length == 0) problem = "Paste the key value.";
                if (problem != null)
                {
                    a.Cancel = true;
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                }
            };
            dlg.Opened += (_, _) => name.Focus(FocusState.Programmatic);

            var res = await dlg.ShowAsync();

            draft.Name = name.Text; draft.Ref = refBox.Text; draft.Service = service.Text; draft.Tags = tags.Text; draft.Value = value.Text;
            draft.Notes = notes.Text; draft.Expires = expires.Date; draft.Favorite = fav.IsChecked == true;

            if (more)
            {
                var picked = await PickIconAsync(draft.Icon);
                if (picked != null) draft.Icon = picked;
                continue;
            }
            if (res == ContentDialogResult.Secondary && existing != null) return new EditResult(existing, null, true);
            if (res != ContentDialogResult.Primary) return null;

            var entry = existing?.Clone() ?? new KeyEntry();
            entry.Name = draft.Name.Trim();
            entry.Ref = draft.Ref.Trim().Length > 0 ? draft.Ref.Trim() : KeyRefs.Slug(entry.Name);
            entry.Service = draft.Service.Trim();
            entry.Tags = draft.Tags.Split(new[] { ',', ';', '،' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            entry.Notes = draft.Notes.Trim();
            entry.Favorite = draft.Favorite;
            entry.Expires = draft.Expires?.Date;
            entry.Icon = draft.Icon;
            return new EditResult(entry, draft.Value.Length > 0 ? draft.Value : null, false);
        }
    }

    // ───────────────────────── authentication ─────────────────────────
    /// <summary>
    /// Shows the unlock dialog (or the create-vault dialog on first use).
    /// true  = unlocked; the progress view is left on screen for the caller to replace.
    /// false = cancelled or failed; the list view is shown.
    /// </summary>
    private async Task<bool> AuthenticateAsync(string title)
    {
        ShowList();
        if (!KeyVault.Exists) return await CreateVaultAsync();

        var helloReady = KeyVault.HelloEnabled && await KeyVault.IsHelloSupportedAsync();
        var error = "";

        while (true)
        {
            var pw = new PasswordBox { PlaceholderText = "Master password", MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
            var err = new TextBlock
            {
                Text = error, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                Visibility = error.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            var dlg = NewDialog();
            dlg.Title = title;
            dlg.Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    AuthHeader(title, helloReady ? "Use Windows Hello, or type your master password." : "Type your master password."),
                    pw, err,
                },
            };
            dlg.SecondaryButtonText = "Unlock";
            dlg.CloseButtonText = "Cancel";
            if (helloReady)
            {
                dlg.PrimaryButtonText = "Windows Hello";
                dlg.DefaultButton = ContentDialogButton.Primary;
            }
            else dlg.DefaultButton = ContentDialogButton.Secondary;

            var viaEnter = false;
            pw.KeyDown += (_, k) =>
            {
                if (k.Key == Windows.System.VirtualKey.Enter) { viaEnter = true; dlg.Hide(); }
            };
            dlg.Opened += (_, _) => { if (!helloReady) pw.Focus(FocusState.Programmatic); };

            var result = await dlg.ShowAsync();
            if (viaEnter) result = ContentDialogResult.Secondary;
            if (result == ContentDialogResult.None) return false;   // Cancel

            if (result == ContentDialogResult.Primary)
            {
                ShowBusy("Waiting for Windows Hello …");
                if (await KeyVault.UnlockWithHelloAsync()) return true;
                ShowList();
                error = "Windows Hello did not unlock the vault. Use your master password.";
                continue;
            }

            var typed = pw.Password;
            if (typed.Length == 0) { error = "Type your master password."; continue; }

            ShowBusy("Unlocking …");
            if (await Task.Run(() => KeyVault.UnlockWithPassword(typed))) return true;
            ShowList();
            error = "Wrong password.";
        }
    }

    private async Task<bool> CreateVaultAsync()
    {
        var p1 = new PasswordBox { Header = "Master password (at least 8 characters)", MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
        var p2 = new PasswordBox { Header = "Repeat the password", MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
        var err = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var dlg = NewDialog();
        dlg.Title = "Create your Keys Store";
        dlg.PrimaryButtonText = "Create";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;
        dlg.Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                AuthHeader("Create your master password",
                    "This password protects all your other keys. It cannot be recovered — if you forget it, the keys are gone."),
                p1, p2, err,
            },
        };
        dlg.PrimaryButtonClick += (_, a) =>
        {
            string? problem = p1.Password.Length < 8 ? "Use at least 8 characters."
                            : p1.Password != p2.Password ? "The two passwords are different." : null;
            if (problem == null) return;
            a.Cancel = true;
            err.Text = problem;
            err.Visibility = Visibility.Visible;
        };
        dlg.Opened += (_, _) => p1.Focus(FocusState.Programmatic);

        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return false;

        var password = p1.Password;
        ShowBusy("Creating the encrypted vault …");
        await Task.Run(() => KeyVault.Create(password));
        return true;
    }

    // ───────────────────────── security dialog (gear button) ─────────────────────────
    private async Task SecurityAsync()
    {
        Status.Text = "";
        var exists = KeyVault.Exists;
        var open = KeyVault.IsUnlocked;
        var hello = KeyVault.HelloEnabled;

        Func<Task>? next = null;       // a follow-up flow that needs its own dialog, run after this one closes
        var dlg = NewDialog();

        var state = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Text = !exists ? "No vault yet — add your first key to create one." : open ? "The vault is unlocked." : "The vault is locked.",
        };
        var lockBtn = Btn("Lock now", () => { KeyVault.Lock(); dlg.Hide(); });
        lockBtn.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        var helloInfo = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        helloInfo.Text = !_helloSupported && !hello
            ? "Windows Hello is not available on this PC (set up a PIN, fingerprint or face in Windows Settings first)."
            : hello ? "Windows Hello can unlock the vault. Your master password always works too."
                    : "Unlock with fingerprint, face or PIN instead of typing the master password.";
        var helloBtn = Btn(hello ? "Disable Windows Hello" : "Enable Windows Hello", () => { next = ToggleHelloAsync; dlg.Hide(); });
        helloBtn.IsEnabled = exists && (_helloSupported || hello);

        var changeBtn = Btn("Change master password", () => { next = ChangePasswordAsync; dlg.Hide(); });
        changeBtn.IsEnabled = exists;
        var exportBtn = Btn("Export backup", () => { next = ExportAsync; dlg.Hide(); });
        exportBtn.IsEnabled = exists;
        var importBtn = Btn("Import backup", () => { next = ImportAsync; dlg.Hide(); });
        var importTextBtn = Btn("Import from .env or JSON", () => { next = ImportTextAsync; dlg.Hide(); });

        var minutes = new[] { 1, 5, 15, 30, 60 };
        var auto = Combo(minutes.Select(m => m == 1 ? "1 minute" : $"{m} minutes").ToArray());
        var cur = Array.IndexOf(minutes, KeyVault.AutoLockMinutes);
        auto.SelectedIndex = cur < 0 ? 1 : cur;
        auto.SelectionChanged += (_, _) =>
        {
            if (auto.SelectedIndex >= 0) SettingsStore.Set("keys.autolock", minutes[auto.SelectedIndex].ToString());
        };

        var ask = Check("Ask for Windows Hello / password every time I open a key", SettingsStore.Get("keys.askEvery", "off") == "on");
        ask.Checked += (_, _) => SettingsStore.Set("keys.askEvery", "on");
        ask.Unchecked += (_, _) => SettingsStore.Set("keys.askEvery", "off");

        dlg.Title = "Security";
        dlg.CloseButtonText = "Close";
        dlg.DefaultButton = ContentDialogButton.Close;
        dlg.Content = new ScrollViewer
        {
            MaxHeight = 520,
            Content = new StackPanel
            {
                Spacing = 12, Width = 460,
                Children =
                {
                    Row(state, lockBtn),
                    helloInfo, helloBtn,
                    Row(Label("Lock automatically after"), auto),
                    ask,
                    changeBtn,
                    new TextBlock { Text = "Backup", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) },
                    new TextBlock { Text = "A backup is the encrypted vault in one file. Keep it together with your master password: you need both to restore it.", Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap },
                    Row(exportBtn, importBtn),
                    importTextBtn,
                    new TextBlock
                    {
                        Text = "Values are encrypted with AES-256-GCM; the key is derived from your master password (PBKDF2, 600 000 rounds) and is never written to disk. "
                             + "Names, services and tags are stored unencrypted so the cards can be searched while locked. "
                             + "The vault also locks by itself when Windows locks or goes to sleep.",
                        Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };

        await dlg.ShowAsync();
        if (next != null) await next();
    }

    private async Task ToggleHelloAsync()
    {
        Status.Text = "";
        if (KeyVault.HelloEnabled)
        {
            await KeyVault.DisableHelloAsync();
            Status.Text = "Windows Hello turned off for the Keys Store.";
            return;
        }
        if (!KeyVault.IsUnlocked)
        {
            if (!await AuthenticateAsync("Unlock to set up Windows Hello")) { ShowList(); return; }
        }
        ShowBusy("Setting up Windows Hello — follow the prompt …");
        try
        {
            await KeyVault.EnableHelloAsync();
            Status.Text = "Windows Hello is on. Next time you can unlock with it.";
        }
        finally { ShowList(); }
    }

    private async Task ChangePasswordAsync()
    {
        Status.Text = "";
        if (!KeyVault.Exists) { Status.Text = "There is no vault yet."; return; }
        if (!KeyVault.IsUnlocked)
        {
            if (!await AuthenticateAsync("Unlock to change the password")) { ShowList(); return; }
            ShowList();
        }

        var current = new PasswordBox { Header = "Current password", MinWidth = 340 };
        var next = new PasswordBox { Header = "New password (at least 8 characters)", MinWidth = 340 };
        var again = new PasswordBox { Header = "Repeat the new password", MinWidth = 340 };
        var err = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var dlg = NewDialog();
        dlg.Title = "Change master password";
        dlg.PrimaryButtonText = "Change";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;
        dlg.Content = new StackPanel { Spacing = 10, Children = { current, next, again, err } };
        dlg.PrimaryButtonClick += async (_, a) =>
        {
            var deferral = a.GetDeferral();
            try
            {
                string? problem = next.Password.Length < 8 ? "Use at least 8 characters."
                                : next.Password != again.Password ? "The new passwords are different." : null;
                if (problem == null)
                {
                    var (cur, nw) = (current.Password, next.Password);
                    if (!await Task.Run(() => KeyVault.ChangePassword(cur, nw))) problem = "The current password is wrong.";
                }
                if (problem != null)
                {
                    a.Cancel = true;
                    err.Text = problem;
                    err.Visibility = Visibility.Visible;
                }
            }
            finally { deferral.Complete(); }
        };
        dlg.Opened += (_, _) => current.Focus(FocusState.Programmatic);

        if (await dlg.ShowAsync() == ContentDialogResult.Primary) Status.Text = "Master password changed.";
    }

    // ───────────────────────── backup ─────────────────────────
    private async Task ExportAsync()
    {
        Status.Text = "";
        if (!KeyVault.Exists) { Status.Text = "There is no vault to back up yet."; return; }

        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = $"ASD-keys-{DateTime.Now:yyyyMMdd}" };
        picker.FileTypeChoices.Add("ASD key vault backup", new List<string> { ".asdvault" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        KeyVault.ExportTo(file.Path);
        Status.Text = $"Backup saved to {file.Path}. Keep it together with your master password.";
    }

    private async Task ImportAsync()
    {
        Status.Text = "";
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        picker.FileTypeFilter.Add(".asdvault");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        var path = file.Path;

        if (!KeyVault.Exists)
        {
            KeyVault.InstallFrom(path);      // no vault on this PC yet: the backup becomes the vault
            RebuildCards();
            Status.Text = "Backup restored. Open a key and unlock it with the backup's master password.";
            return;
        }

        if (!KeyVault.IsUnlocked)
        {
            if (!await AuthenticateAsync("Unlock to import a backup")) { ShowList(); return; }
            ShowList();
        }

        var pw = new PasswordBox { Header = "Master password of the backup", MinWidth = 340, HorizontalAlignment = HorizontalAlignment.Stretch };
        var err = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        (int Added, int Skipped)? outcome = null;

        var dlg = NewDialog();
        dlg.Title = "Import backup";
        dlg.PrimaryButtonText = "Import";
        dlg.CloseButtonText = "Cancel";
        dlg.DefaultButton = ContentDialogButton.Primary;
        dlg.Content = new StackPanel
        {
            Spacing = 10, Width = 400,
            Children =
            {
                new TextBlock { Text = "Keys that are not in this vault yet are added. Keys that already exist are left untouched.", TextWrapping = TextWrapping.Wrap },
                pw, err,
            },
        };
        dlg.PrimaryButtonClick += async (_, a) =>
        {
            var deferral = a.GetDeferral();
            try
            {
                var typed = pw.Password;
                string? problem = null;
                if (typed.Length == 0) problem = "Type the backup's master password.";
                else
                {
                    outcome = await Task.Run(() => KeyVault.MergeFrom(path, typed));
                    if (outcome == null) problem = "Wrong password for this backup.";
                }
                if (problem != null) { a.Cancel = true; err.Text = problem; err.Visibility = Visibility.Visible; }
            }
            catch (Exception ex) { a.Cancel = true; err.Text = ex.Message; err.Visibility = Visibility.Visible; }
            finally { deferral.Complete(); }
        };
        dlg.Opened += (_, _) => pw.Focus(FocusState.Programmatic);

        if (await dlg.ShowAsync() == ContentDialogResult.Primary && outcome != null)
            RebuildCards();
        if (outcome is { } r2)
            Status.Text = $"Imported {r2.Added} key{(r2.Added == 1 ? "" : "s")}; {r2.Skipped} already existed or could not be read.";
    }

    // ───────────────────────── import from .env / JSON ─────────────────────────
    private async Task ImportTextAsync()
    {
        Status.Text = "";
        if (!KeyVault.IsUnlocked)
        {
            if (!await AuthenticateAsync("Unlock to import keys")) { ShowList(); return; }
            ShowList();
        }

        var text = "";
        while (true)      // loops again after "Choose file …" so the text is kept
        {
            var existing = KeyVault.All().Select(KeyRefs.RefOf).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = new List<ImportItem>();

            var box = new TextBox
            {
                Header = "Paste .env lines or JSON", Text = text, AcceptsReturn = true, Height = 200, IsSpellCheckEnabled = false,
                TextWrapping = TextWrapping.NoWrap, FontFamily = new FontFamily("Consolas"),
            };
            ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            ScrollViewer.SetHorizontalScrollBarVisibility(box, ScrollBarVisibility.Auto);
            var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };

            var pickFile = false;
            var dlg = NewDialog();
            dlg.Title = "Import keys from .env or JSON";
            dlg.PrimaryButtonText = "Import";
            dlg.SecondaryButtonText = "Choose file…";
            dlg.CloseButtonText = "Cancel";
            dlg.DefaultButton = ContentDialogButton.Primary;
            dlg.Content = new StackPanel
            {
                Spacing = 10, Width = 480,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Lines like NAME=value (a .env file), or JSON like {\"NAME\": \"value\"}. Each name becomes a key, with the name as its string value. "
                             + "Names that already exist are skipped. Delete the source file afterwards.",
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12,
                    },
                    box, summary,
                },
            };

            void Refresh()
            {
                fresh.Clear();
                try
                {
                    var seen = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);
                    var skipped = 0;
                    foreach (var item in KeyImport.Parse(box.Text))
                    {
                        var reference = KeyRefs.Slug(item.Name);
                        if (reference.Length == 0 || !seen.Add(reference)) { skipped++; continue; }
                        fresh.Add(item);
                    }
                    if (box.Text.Trim().Length == 0) summary.Text = "";
                    else if (fresh.Count == 0 && skipped == 0) summary.Text = "No keys found. Use lines like NAME=value.";
                    else
                    {
                        var names = string.Join(", ", fresh.Take(6).Select(i => i.Name)) + (fresh.Count > 6 ? ", …" : "");
                        summary.Text = $"{fresh.Count} new key{(fresh.Count == 1 ? "" : "s")}" + (fresh.Count > 0 ? ": " + names : "")
                                     + (skipped > 0 ? $"   ·   {skipped} skipped (already exist or repeated)" : "");
                    }
                }
                catch (Exception ex) { summary.Text = "Cannot read this text: " + ex.Message; }
                dlg.IsPrimaryButtonEnabled = fresh.Count > 0;
            }
            box.TextChanged += (_, _) => Refresh();
            Refresh();

            dlg.SecondaryButtonClick += (_, _) => pickFile = true;
            dlg.Opened += (_, _) => box.Focus(FocusState.Programmatic);

            var res = await dlg.ShowAsync();
            text = box.Text;

            if (pickFile)
            {
                try
                {
                    var picker = new Windows.Storage.Pickers.FileOpenPicker();
                    picker.FileTypeFilter.Add("*");
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
                    var file = await picker.PickSingleFileAsync();
                    if (file != null)
                    {
                        var info = await file.GetBasicPropertiesAsync();
                        if (info.Size > 1_000_000) Status.Text = "That file is too big to be a key list.";
                        else text = await Windows.Storage.FileIO.ReadTextAsync(file);
                    }
                }
                catch (Exception ex) { Status.Text = "Could not read the file: " + ex.Message; }
                continue;
            }
            if (res != ContentDialogResult.Primary || fresh.Count == 0) return;

            foreach (var item in fresh)
            {
                var entry = new KeyEntry
                {
                    Name = item.Name, Ref = KeyRefs.Slug(item.Name), Service = item.Service, Notes = item.Notes,
                    Tags = new List<string> { "imported" },
                };
                KeyVault.Upsert(entry, item.Value);
            }
            RebuildCards();
            Status.Text = $"Imported {fresh.Count} key{(fresh.Count == 1 ? "" : "s")}.";
            return;
        }
    }

    // ───────────────────────── clipboard ─────────────────────────
    private async void CopySecret(string secret)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(secret);
            // keep it out of the Win+V clipboard history
            Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false });
            KeyVault.Touch();
            Status.Text = "Copied. The clipboard will be cleared in 30 seconds.";

            var token = ++_copyToken;
            await Task.Delay(TimeSpan.FromSeconds(30));
            if (token != _copyToken) return;

            var view = Clipboard.GetContent();
            if (view.Contains(StandardDataFormats.Text) && await view.GetTextAsync() == secret)
            {
                Clipboard.Clear();
                if (IsLoaded) Status.Text = "Clipboard cleared.";
            }
        }
        catch (Exception ex) { Fail(ex); }
    }
}
