using System;
using System.Globalization;
using System.Resources;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows.Forms;

namespace RBX_Alt_Manager.Classes
{
    /// <summary>
    /// Runtime culture layer for the legacy WinForms surface. The designer files remain the English source of
    /// truth; Russian strings live in a real satellite .resx and are applied only to caption-like controls, never
    /// to text boxes that may contain usernames, ids, scripts or other user data.
    /// </summary>
    internal static class LegacyI18n
    {
        private sealed class Original { public string Text; }
        private sealed class Seen { }

        private static readonly System.Resources.ResourceManager Strings = new System.Resources.ResourceManager("RBX_Alt_Manager.Localization.LegacyStrings", typeof(LegacyI18n).Assembly);
        private static readonly ConditionalWeakTable<object, Original> Originals = new ConditionalWeakTable<object, Original>();
        private static readonly ConditionalWeakTable<Form, Seen> SeenForms = new ConditionalWeakTable<Form, Seen>();
        private static bool Started;
        private static CultureInfo Culture = CultureInfo.GetCultureInfo("en");

        public static string Language => Culture.TwoLetterISOLanguageName;

        public static void Start()
        {
            if (Started) return;
            Started = true;
            SetLanguage(ReadLanguage());
            Application.Idle += OnIdle;
        }

        public static void SetLanguage(string Language)
        {
            Culture = string.Equals(Language, "ru", StringComparison.OrdinalIgnoreCase)
                ? CultureInfo.GetCultureInfo("ru")
                : CultureInfo.GetCultureInfo("en");

            Thread.CurrentThread.CurrentUICulture = Culture;

            foreach (Form Form in Application.OpenForms)
                Apply(Form);
        }

        private static void OnIdle(object Sender, EventArgs Args)
        {
            string Wanted = ReadLanguage();
            if (!string.Equals(Wanted, Language, StringComparison.OrdinalIgnoreCase)) SetLanguage(Wanted);

            foreach (Form Form in Application.OpenForms)
            {
                if (SeenForms.TryGetValue(Form, out _)) continue;
                SeenForms.Add(Form, new Seen());
                Apply(Form);
            }
        }

        public static void Apply(Form Form)
        {
            if (Form == null || Form.IsDisposed) return;
            TranslateControl(Form);
            TranslateToolStrip(Form.MainMenuStrip);
        }

        private static void TranslateControl(Control Control)
        {
            if (Control == null) return;

            if (ShouldTranslate(Control)) SetText(Control, Control.Text);

            if (Control is ToolStrip ToolStrip) TranslateToolStrip(ToolStrip);
            if (Control.ContextMenuStrip != null) TranslateToolStrip(Control.ContextMenuStrip);

            foreach (Control Child in Control.Controls) TranslateControl(Child);
        }

        private static bool ShouldTranslate(Control Control) =>
            Control is Form || Control is Label || Control is Button || Control is CheckBox || Control is RadioButton ||
            Control is GroupBox || Control is TabPage || Control is LinkLabel;

        private static void TranslateToolStrip(ToolStrip Strip)
        {
            if (Strip == null) return;
            foreach (ToolStripItem Item in Strip.Items) TranslateItem(Item);
        }

        private static void TranslateItem(ToolStripItem Item)
        {
            if (Item == null) return;
            SetText(Item, Item.Text);
            if (Item is ToolStripDropDownItem DropDown)
                foreach (ToolStripItem Child in DropDown.DropDownItems) TranslateItem(Child);
        }

        private static void SetText(Control Control, string Current)
        {
            Original Original = Originals.GetValue(Control, _ => new Original { Text = Current ?? string.Empty });
            Control.Text = Translate(Original.Text);
        }

        private static void SetText(ToolStripItem Item, string Current)
        {
            Original Original = Originals.GetValue(Item, _ => new Original { Text = Current ?? string.Empty });
            Item.Text = Translate(Original.Text);
        }

        private static string Translate(string English)
        {
            if (Culture.TwoLetterISOLanguageName != "ru" || string.IsNullOrEmpty(English)) return English;
            try { return Strings.GetString(English, Culture) ?? English; }
            catch (MissingManifestResourceException) { return English; }
        }

        private static string ReadLanguage()
        {
            try { return AccountManager.General != null && AccountManager.General.Exists("UiLanguage") ? AccountManager.General.Get("UiLanguage") : "en"; }
            catch { return "en"; }
        }
    }
}
