using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ASD;

/// <summary>
/// The number on ASD's taskbar icon: how many notifications have not been read yet.
/// Only the installed (MSIX) ASD has the identity Windows needs for a badge; the plain folder version does nothing.
/// </summary>
internal static class TaskbarBadge
{
    public static void Set(int count)
    {
        if (!AppIdentity.IsPackaged) return;
        try
        {
            var updater = BadgeUpdateManager.CreateBadgeUpdaterForApplication();
            if (count <= 0) { updater.Clear(); return; }

            var xml = BadgeUpdateManager.GetTemplateContent(BadgeTemplateType.BadgeNumber);
            ((XmlElement)xml.SelectSingleNode("/badge")).SetAttribute("value", Math.Min(count, 99).ToString());
            updater.Update(new BadgeNotification(xml));
        }
        catch { /* a badge is only a hint */ }
    }
}
