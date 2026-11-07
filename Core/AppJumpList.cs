using Windows.UI.StartScreen;

namespace ASD;

/// <summary>
/// The quick tasks in the menu of ASD's taskbar icon (right-click) and in the Start menu. Each item starts ASD with
/// "--open &lt;tool&gt;" (see <see cref="LaunchRouter"/>); if ASD is already open, that one comes to the front on that tool.
/// Only the installed (MSIX) ASD has the identity Windows needs for a Jump List.
/// </summary>
internal static class AppJumpList
{
    public static async Task SetupAsync()
    {
        if (!AppIdentity.IsPackaged) return;
        try
        {
            if (!JumpList.IsSupported()) return;

            var list = await JumpList.LoadCurrentAsync();
            list.SystemGroupKind = JumpListSystemGroupKind.None;   // only these tasks (no "Recent" / "Frequent" lists)
            list.Items.Clear();
            list.Items.Add(Item("keys", "Keys Store", "Open the encrypted vault for your API keys"));
            list.Items.Add(Item("automation", "Automation", "Cards joined by arrows"));
            list.Items.Add(Item("extensions", "Extensions", "Add more tools to ASD"));
            list.Items.Add(Item("updates", "Check for updates", "See if a newer ASD is available"));
            await list.SaveAsync();
        }
        catch { /* a jump list is a convenience */ }
    }

    private static JumpListItem Item(string tool, string title, string description)
    {
        var item = JumpListItem.CreateWithArguments("--open " + tool, title);
        item.Description = description;
        try { item.Logo = new Uri($"ms-appx:///Assets/JumpList/{tool}.png"); } catch { /* no icon: the text is still there */ }
        item.GroupName = "";   // the empty group is "Tasks"
        return item;
    }
}
