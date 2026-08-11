using System.Text;
using System.Windows;

namespace RateDesk.Weekly
{
    /// <summary>CF_HTML clipboard writer: the header's byte offsets are what Outlook/Word actually
    /// parse, computed over UTF-8 — get them wrong and the paste is silently empty.
    /// Ported verbatim from dodgeball's standalone weekly app (2026-08-11 consolidation).</summary>
    internal static class ClipboardHtml
    {
        public static void Set(string htmlFragment, string plainText)
        {
            const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            const string pre = "<html><body>\r\n<!--StartFragment-->";
            const string post = "<!--EndFragment-->\r\n</body></html>";
            string dummy = string.Format(header, 0, 0, 0, 0);
            int startHtml = Encoding.UTF8.GetByteCount(dummy);
            int startFrag = startHtml + Encoding.UTF8.GetByteCount(pre);
            int endFrag = startFrag + Encoding.UTF8.GetByteCount(htmlFragment);
            int endHtml = endFrag + Encoding.UTF8.GetByteCount(post);
            string cf = string.Format(header, startHtml, endHtml, startFrag, endFrag) + pre + htmlFragment + post;

            var obj = new DataObject();
            obj.SetData(DataFormats.Html, cf);
            obj.SetText(plainText, TextDataFormat.UnicodeText);
            Clipboard.SetDataObject(obj, true);
        }
    }
}
