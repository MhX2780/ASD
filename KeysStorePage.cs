using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace ASD;

/// <summary>
/// Encrypted vault for API keys. Cards (name, service, tags) are visible and searchable at all times;
/// the secret itself is only decrypted after the user proves who they are:
///   click a card → dialog (Windows Hello / Password / Cancel) → content hides → progress ring → secret shown + Copy.
/// Once unlocked, the vault stays open until "Lock now" or the idle timeout, and further clicks skip the dialog
/// (unless "ask every time" is on in Security).
/// </summary>
public sealed class KeysStorePage : ToolPage
{
    private const string AllTags = "All tags";
    private const string AllServices = "All services";

    // the three mutually-exclusive views
    private readonly StackPanel _listView = new() { Spacing = 12 };
    private readonly StackPanel _busyView = new()
    {
        Spacing = 16, Visibility = Visibility.Collapsed,
        HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 70, 0, 70),
    };
    private readonly StackPanel _revealView = new() { Spacing = 12, Visibility = Visibility.Collapsed };

    private readonly TextBlock _busyText = new() { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 };
    private readonly TextBox _search;
    private readonly ComboBox _tagFilter = Combo(AllTags);
    private readonly ComboBox _serviceFilter = Combo(AllServices);
    private readonly ComboBox _stateFilter = Combo("All keys", "Favorites", "Expired", "Expiring in 30 days", "Has expiry", "No expiry");
    private readonly CheckBox _inValues = Check("Also search inside values (while unlocked)");
    private readonly StackPanel _cardsHost = new() { Spacing = 8 };
    private readonly TextBlock _count = new() { Opacity = 0.7 };
    private readonly TextBlock _lockState = new() { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly Button _lockBtn;
    private readonly Button _helloBtn;
    private readonly TextBlock _helloState = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

    private readonly DispatcherTimer _revealTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    private bool _flow;          // a dialog / auth flow is running
    private bool _suspend;       // ignore filter events while refilling the combos
    private bool _helloSupported;
    private int _copyToken;

    public KeysStorePage() : base("keys", "Keys Store",
        "Your API keys, encrypted with AES-256. Names and tags stay searchable; a value is only decrypted after Windows Hello or your master password.")
    {
        _search = Input("search", "", 0, "Search name, service, tags, notes …", persist: false, multiline: false);
        _search.TextChanged += (_, _) => RebuildCards();

        foreach (var c in new[] { _tagFilter, _serviceFilter, _stateFilter })
            c.SelectionChanged += (_, _) => { if (!_suspend) RebuildCards(); };
        _inValues.Checked += (_, _) => RebuildCards();
        _inValues.Unchecked += (_, _) => RebuildCards();

        _lockBtn = Btn("Lock now", () => KeyVault.Lock());
        _helloBtn = Btn("Enable Windows Hello", () => Run(ToggleHelloAsync));

        _revealTimer.Tick += (_, _) => { _revealTimer.Stop(); ShowList(); Status.Text = "Hidden automatically."; };

        _busyView.Children.Add(new ProgressRing { IsActive = true, Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center });
        _busyView.Children.Add(_busyText);

        _listView.Children.Add(Row(Btn("Add key", () => Run(AddAsync), accent: true), _lockState, _lockBtn));
        _listView.Children.Add(_search);
        _listView.Children.Add(Row(_tagFilter, _serviceFilter, _stateFilter));
        _listView.Children.Add(_inValues);
        _listView.Children.Add(_count);
        _listView.Children.Add(_cardsHost);
        _listView.Children.Add(BuildSecurity());

        Body.Children.Add(_listView);
        Body.Children.Add(_busyView);
        Body.Children.Add(_revealView);

        Loaded += async (_, _) =>
        {
            KeyVault.Locked += OnVaultLocked;
            try
            {
                _helloSupported = await KeyVault.IsHelloSupportedAsync();
                RefreshFilters();
                RebuildCards();
                UpdateLockUi();
            }
            catch (Exception ex) { Fail(ex); }
        };
        Unloaded += (_, _) =>
        {
            KeyVault.Locked -= OnVaultLocked;
            _revealTimer.Stop();
            _revealView.Children.Clear();
        };

        try { RefreshFilters(); RebuildCards(); UpdateLockUi(); }
        catch (Exception ex) { Fail(ex); }
    }

    // ───────────────────────── views ─────────────────────────
    private void ShowList()
    {
        _revealTimer.Stop();
        _revealView.Children.Clear();       // drop any decrypted value from the UI
        _busyView.Visibility = Visibility.Collapsed;
        _revealView.Visibility = Visibility.Collapsed;
        _listView.Visibility = Visibility.Visible;
        UpdateLockUi();
    }

    private void ShowBusy(string text)
    {
        _revealTimer.Stop();
        _busyText.Text = text;
        _listView.Visibility = Visibility.Collapsed;
        _revealView.Visibility = Visibility.Collapsed;
        _busyView.Visibility = Visibility.Visible;
    }

    private void ShowReveal(KeyEntry e, string secret)
    {
        _revealView.Children.Clear();

        var meta = string.Join("  ·  ", new[] { e.Service }.Concat(e.Tags).Where(s => s.Length > 0));
        var valueBox = new TextBox
        {
            Text = secret, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), MinHeight = 90, MaxHeight = 280,
            Header = "Value",
        };
        ScrollViewer.SetVerticalScrollBarVisibility(valueBox, ScrollBarVisibility.Auto);

        var shown = true;
        Button? toggle = null;
        toggle = Btn("Hide", () =>
        {
            shown = !shown;
            valueBox.Text = shown ? secret : new string('•', Math.Clamp(secret.Length, 8, 32));
            toggle!.Content = shown ? "Hide" : "Show";
        });

        var info = new List<string>();
        if (e.Expires is DateTime exp) info.Add($"Expires {exp:yyyy-MM-dd}");
        info.Add($"Added {e.Created:yyyy-MM-dd HH:mm}");
        info.Add($"Changed {e.Modified:yyyy-MM-dd HH:mm}");

        _revealView.Children.Add(Row(Btn("← Back", ShowList)));
        _revealView.Children.Add(new TextBlock { Text = e.Name, FontSize = 22, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap });
        if (meta.Length > 0) _revealView.Children.Add(new TextBlock { Text = meta, Opacity = 0.75, TextWrapping = TextWrapping.Wrap });
        _revealView.Children.Add(valueBox);
        _revealView.Children.Add(Row(
            Btn("Copy", () => CopySecret(secret), accent: true),
            toggle,
            Btn("Edit", () => { ShowList(); Run(() => EditAsync(e.Id)); })));
        if (e.Notes.Length > 0)
            _revealView.Children.Add(new TextBlock { Text = e.Notes, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        _revealView.Children.Add(new TextBlock { Text = string.Join("   ·   ", info), Opacity = 0.6, FontSize = 12 });
        _revealView.Children.Add(new TextBlock
        {
            Text = "This view hides itself after 60 seconds. The clipboard is cleared 30 seconds after you copy.",
            Opacity = 0.6, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });

        _listView.Visibility = Visibility.Collapsed;
        _busyView.Visibility = Visibility.Collapsed;
        _revealView.Visibility = Visibility.Visible;
        _revealTimer.Stop();
        _revealTimer.Start();
        CursorHelper.ApplyHandCursorToButtons(_revealView);
    }

    private void UpdateLockUi()
    {
        var open = KeyVault.IsUnlocked;
        _lockState.Text = !KeyVault.Exists ? "No vault yet" : open ? "Unlocked" : "Locked";
        _lockState.Opacity = open ? 1 : 0.7;
        _lockBtn.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        _inValues.IsEnabled = open;
        if (!open && _inValues.IsChecked == true) _inValues.IsChecked = false;

        var hello = KeyVault.HelloEnabled;
        _helloBtn.Content = hello ? "Disable Windows Hello" : "Enable Windows Hello";
        _helloBtn.IsEnabled = _helloSupported || hello;
        _helloState.Text = !_helloSupported && !hello
            ? "Windows Hello is not available on this PC (set up a PIN, fingerprint or face in Windows Settings first)."
            : hello ? "Windows Hello can unlock the vault. Your master password always works too."
                    : "Unlock with fingerprint, face or PIN instead of typing the master password.";
    }

    private void OnVaultLocked() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_revealView.Visibility == Visibility.Visible) ShowList();
        _revealView.Children.Clear();
        UpdateLockUi();
        RebuildCards();
        Status.Text = "Vault locked.";
    });

    // ───────────────────────── list / search / filter ─────────────────────────
    private void RefreshFilters()
    {
        var all = KeyVault.All();
        _suspend = true;
        try
        {
            Refill(_tagFilter, AllTags, all.SelectMany(e => e.Tags));
            Refill(_serviceFilter, AllServices, all.Select(e => e.Service));
        }
        finally { _suspend = false; }
    }

    private static void Refill(ComboBox box, string first, IEnumerable<string> values)
    {
        var keep = box.SelectedItem as string;
        box.Items.Clear();
        box.Items.Add(first);
        foreach (var v in values.Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase))
            box.Items.Add(v);
        var idx = keep == null ? 0 : box.Items.IndexOf(keep);
        box.SelectedIndex = idx < 0 ? 0 : idx;
    }

    private IEnumerable<KeyEntry> Filtered(List<KeyEntry> all)
    {
        var terms = (_search.Text ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var tag = Sel(_tagFilter);
        var service = Sel(_serviceFilter);
        var state = Sel(_stateFilter);
        var inValues = Is(_inValues) && KeyVault.IsUnlocked;
        var today = DateTime.Today;

        foreach (var e in all)
        {
            if (tag != AllTags && !e.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase)) continue;
            if (service != AllServices && !string.Equals(e.Service, service, StringComparison.OrdinalIgnoreCase)) continue;

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
                var hay = $"{e.Name} {e.Service} {string.Join(' ', e.Tags)} {e.Notes}";
                if (inValues) hay += " " + (KeyVault.TryReveal(e.Id) ?? "");
                hay = hay.ToLowerInvariant();
                if (!terms.All(hay.Contains)) continue;
            }
            yield return e;
        }
    }

    private void RebuildCards()
    {
        try
        {
            if (_inValues.IsChecked == true) KeyVault.Touch();
            var all = KeyVault.All();
            var shown = Filtered(all).OrderByDescending(e => e.Favorite).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

            _count.Text = all.Count == 0 ? "" : $"{shown.Count} of {all.Count} keys";
            _cardsHost.Children.Clear();
            if (all.Count == 0)
            {
                _cardsHost.Children.Add(new TextBlock
                {
                    Text = KeyVault.Exists
                        ? "No keys yet. Press “Add key”."
                        : "No keys yet. Press “Add key” — you will be asked to create a master password first.",
                    Opacity = 0.7, TextWrapping = TextWrapping.Wrap,
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

    private UIElement Card(KeyEntry e)
    {
        var meta = string.Join("  ·  ", new[] { e.Service }.Concat(e.Tags).Where(s => s.Length > 0));
        var (expiryText, level) = ExpiryInfo(e);

        var content = new StackPanel
        {
            Spacing = 3, Margin = new Thickness(0, 0, 58, 0), HorizontalAlignment = HorizontalAlignment.Left,
            Children =
            {
                new FontIcon { Glyph = "", FontSize = 22, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) },
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
        main.Click += (_, _) => Run(() => RevealAsync(e.Id));

        var star = SmallIconButton(e.Favorite ? "" : "", e.Favorite ? "Remove from favorites" : "Add to favorites");
        star.Click += (_, _) => { KeyVault.SetFavorite(e.Id, !e.Favorite); RebuildCards(); };
        var edit = SmallIconButton("", "Edit or rename");
        edit.Click += (_, _) => Run(() => EditAsync(e.Id));

        var corner = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 0,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 10, 10, 0),
            Children = { edit, star },
        };

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
            try { RefreshFilters(); RebuildCards(); UpdateLockUi(); } catch { /* page may be gone */ }
        }
    }

    private async Task RevealAsync(string id)
    {
        var entry = KeyVault.Find(id);
        if (entry == null) return;
        Status.Text = "";

        var askEvery = SettingsStore.Get("keys.askEvery", "off") == "on";
        if (!KeyVault.IsUnlocked || askEvery)
        {
            if (!await AuthenticateAsync($"Open “{entry.Name}”")) { ShowList(); return; }
        }
        else
        {
            ShowBusy("Decrypting …");
            await Task.Delay(450);
        }

        var secret = KeyVault.Reveal(id);
        ShowReveal(entry, secret);
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
            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Delete “{existing.Name}”?",
                Content = "The key and its encrypted value are removed from this PC. This cannot be undone.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };
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

    private async Task<EditResult?> EditDialogAsync(KeyEntry? existing)
    {
        var isNew = existing == null;
        var name = new TextBox { Header = "Name", PlaceholderText = "e.g. OpenAI – production", Text = existing?.Name ?? "" };
        var service = new TextBox { Header = "Service", PlaceholderText = "e.g. OpenAI, GitHub, Stripe", Text = existing?.Service ?? "" };
        var tags = new TextBox { Header = "Tags (comma separated)", PlaceholderText = "prod, billing", Text = existing == null ? "" : string.Join(", ", existing.Tags) };
        var value = new TextBox
        {
            Header = isNew ? "Value" : "New value (leave empty to keep the current one)",
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 84, IsSpellCheckEnabled = false,
            FontFamily = new FontFamily("Consolas"),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(value, ScrollBarVisibility.Auto);
        var notes = new TextBox { Header = "Notes", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 64, Text = existing?.Notes ?? "" };
        var expires = new CalendarDatePicker { Header = "Expires (optional)", PlaceholderText = "No expiry" };
        if (existing?.Expires is DateTime d) expires.Date = new DateTimeOffset(d.Date);
        var fav = new CheckBox { Content = "Favorite", IsChecked = existing?.Favorite ?? false };
        var error = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };

        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew ? "Add key" : "Edit key",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new ScrollViewer
            {
                MaxHeight = 500,
                Content = new StackPanel { Spacing = 10, Width = 460, Children = { name, service, tags, value, notes, expires, fav, error } },
            },
        };
        if (!isNew) dlg.SecondaryButtonText = "Delete";

        dlg.PrimaryButtonClick += (_, a) =>
        {
            string? problem = null;
            if (name.Text.Trim().Length == 0) problem = "Give the key a name.";
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
        if (res == ContentDialogResult.Secondary && existing != null) return new EditResult(existing, null, true);
        if (res != ContentDialogResult.Primary) return null;

        var entry = existing?.Clone() ?? new KeyEntry();
        entry.Name = name.Text.Trim();
        entry.Service = service.Text.Trim();
        entry.Tags = tags.Text.Split(new[] { ',', ';', '،' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        entry.Notes = notes.Text.Trim();
        entry.Favorite = fav.IsChecked == true;
        entry.Expires = expires.Date?.Date;
        return new EditResult(entry, value.Text.Length > 0 ? value.Text : null, false);
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
            var pw = new PasswordBox { PlaceholderText = "Master password", MinWidth = 340 };
            var err = new TextBlock
            {
                Text = error, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
                Visibility = error.Length > 0 ? Visibility.Visible : Visibility.Collapsed,
            };
            var dlg = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock { Text = helloReady ? "Use Windows Hello, or type your master password." : "Type your master password.", TextWrapping = TextWrapping.Wrap },
                        pw, err,
                    },
                },
                SecondaryButtonText = "Password",
                CloseButtonText = "Cancel",
            };
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
        var p1 = new PasswordBox { Header = "Master password (at least 8 characters)", MinWidth = 340 };
        var p2 = new PasswordBox { Header = "Repeat the password", MinWidth = 340 };
        var err = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.OrangeRed), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Create your Keys Store",
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "This password encrypts every key. It cannot be recovered — if you forget it, the keys are gone.",
                        TextWrapping = TextWrapping.Wrap,
                    },
                    p1, p2, err,
                },
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

    // ───────────────────────── security settings ─────────────────────────
    private UIElement BuildSecurity()
    {
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

        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                _helloState,
                _helloBtn,
                Row(Label("Lock automatically after"), auto, new TextBlock { Text = "without use", Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center }),
                ask,
                Btn("Change master password", () => Run(ChangePasswordAsync)),
                new TextBlock
                {
                    Text = "Values are encrypted with AES-256-GCM; the key is derived from your password (PBKDF2, 600 000 rounds) and never written to disk. "
                         + "Names, services and tags are stored unencrypted so the cards can be searched while locked.",
                    Opacity = 0.65, FontSize = 12, TextWrapping = TextWrapping.Wrap,
                },
            },
        };
        return new Expander
        {
            Header = "Security",
            Content = panel,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
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
        if (!KeyVault.Exists)
        {
            Status.Text = "Add a key first — that creates the vault and its master password.";
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
        var dlg = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Change master password",
            PrimaryButtonText = "Change",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 10, Children = { current, next, again, err } },
        };
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
