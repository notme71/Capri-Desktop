using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace CapriDesktop
{
    // Every piece of text she shows goes through Lang.T with its English wording as the key.
    // No language loaded, or a missing line, simply shows the English text.
    internal static class Lang
    {
        private static Dictionary<string, string> lines = new Dictionary<string, string>();
        private static string folder = "";

        internal static string Code = "en";

        // Code, then the name she shows for it in its own language.
        internal static readonly string[][] Choices =
        {
            new string[] { "en", "English" }, new string[] { "pl", "Polski" }, new string[] { "fr", "Français" },
            new string[] { "de", "Deutsch" }, new string[] { "es", "Español" }, new string[] { "it", "Italiano" },
            new string[] { "pt", "Português" }, new string[] { "ru", "Русский" }, new string[] { "cs", "Čeština" },
            new string[] { "vi", "Tiếng Việt" }
        };

        // The English names, for telling the model which language to answer in.
        private static readonly string[][] Names =
        {
            new string[] { "pl", "Polish" }, new string[] { "fr", "French" }, new string[] { "de", "German" },
            new string[] { "es", "Spanish" }, new string[] { "it", "Italian" }, new string[] { "pt", "Portuguese" },
            new string[] { "ru", "Russian" }, new string[] { "cs", "Czech" }, new string[] { "vi", "Vietnamese" }
        };

        // Added to her personality for the model when a language other than English is chosen; empty for English.
        internal static string Instruction()
        {
            foreach (string[] name in Names)
                if (name[0] == Code)
                    return "\n\nLANGUAGE: The user has set you to " + name[1] + ". Write every reply in " + name[1] + ", even if they write to you in English or another language. Stay in character, keep it just as short, and do not mention this rule.";
            return "";
        }

        // Reads the words built into the app, then any file of your own in the data folder's lang folder, which wins.
        internal static void Load(string code, string data)
        {
            folder = data ?? "";
            bool known = false;
            foreach (string[] choice in Choices) if (choice[0] == code) known = true;
            if (!known) code = "en";
            Dictionary<string, string> found = new Dictionary<string, string>();
            if (code != "en")
            {
                try
                {
                    using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Capri.Lang." + code + ".txt"))
                        if (stream != null) Parse(new StreamReader(stream, Encoding.UTF8).ReadToEnd(), found);
                    string own = Path.Combine(folder, "lang", code + ".txt");
                    if (folder.Length > 0 && File.Exists(own)) Parse(File.ReadAllText(own, Encoding.UTF8), found);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            lines = found;
            Code = code;
        }

        // A line starting with "> " is the English wording; the line after it is the translation. "#" lines are notes.
        internal static void Parse(string text, Dictionary<string, string> into)
        {
            string key = null;
            foreach (string raw in text.Replace("\r\n", "\n").Split('\n'))
            {
                string line = raw.TrimEnd();
                if (line.StartsWith("> ")) { key = Unescape(line.Substring(2)); continue; }
                if (key == null || line.Length == 0 || line.StartsWith("#")) continue;
                into[key] = Unescape(line);
                key = null;
            }
        }

        private static string Unescape(string text)
        {
            StringBuilder result = new StringBuilder();
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\\' && i + 1 < text.Length) { i++; result.Append(text[i] == 'n' ? '\n' : text[i]); }
                else result.Append(text[i]);
            }
            return result.ToString();
        }

        // One line in another language than the one in use, for telling you about the change you just made.
        internal static string In(string code, string english)
        {
            Dictionary<string, string> kept = lines;
            string keptCode = Code;
            Load(code, folder);
            string result = T(english);
            lines = kept;
            Code = keptCode;
            return result;
        }

        internal static string T(string english)
        {
            string found;
            return english != null && lines.TryGetValue(english, out found) && found.Length > 0 ? found : english;
        }

        // Wording that changes with the number, kept as one key: "{0} minute|{0} minutes". Polish, Russian and Czech have a third form, for 5 and up; Vietnamese has one form.
        internal static string Count(string forms, int n)
        {
            string[] parts = T(forms).Split('|');
            int index;
            if (Code == "pl") index = n == 1 ? 0 : (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14)) ? 1 : 2;
            else if (Code == "ru") index = (n % 10 == 1 && n % 100 != 11) ? 0 : (n % 10 >= 2 && n % 10 <= 4 && (n % 100 < 12 || n % 100 > 14)) ? 1 : 2;
            else if (Code == "cs") index = n == 1 ? 0 : (n >= 2 && n <= 4) ? 1 : 2;
            else if (Code == "vi") index = 0;
            else if (Code == "fr" || Code == "pt") index = n < 2 ? 0 : 1;
            else index = n == 1 ? 0 : 1;
            if (index >= parts.Length) index = parts.Length - 1;
            return string.Format(parts[index], n);
        }

        internal static string F(string english, params object[] values)
        {
            try { return string.Format(T(english), values); }
            catch (FormatException) { return string.Format(english, values); }
        }
    }
}
