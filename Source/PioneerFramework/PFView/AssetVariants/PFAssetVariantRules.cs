using System;
using System.Text;

namespace PioneerFramework
{
    /// <summary>
    /// Pure rules for resolving a PFView model variant.
    ///
    /// Naming contract:
    ///   base:    Models/X15/PFL_X15_Hypersonic.obj
    ///   minimal: Models/X15/MIN_PFL_X15_Hypersonic.obj
    ///
    /// The prefix is applied only to the final file-name segment. Directory
    /// separators and file extensions are preserved so the same helper works
    /// for mesh paths and ContentFinder texture paths.
    /// </summary>
    internal static class PFAssetVariantRules
    {
        internal const string DefaultPrefix = "MIN_";
        internal const int MaxPrefixLength = 32;

        private static readonly char[] PathSeparators = { '/', '\\' };

        internal static bool MinimalMode =>
            PioneerFrameworkLiteMod.Settings?.pfviewMinimalMode ?? false;

        internal static string Prefix =>
            NormalizePrefix(PioneerFrameworkLiteMod.Settings?.pfviewMinimalAssetPrefix);

        /// <summary>
        /// Sanitize a user/configuration supplied prefix. Empty or invalid
        /// values fall back to MIN_. Path separators are removed so a prefix
        /// can never escape the asset folder.
        /// </summary>
        internal static string NormalizePrefix(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return DefaultPrefix;

            char[] invalid = System.IO.Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(Math.Min(raw.Length, MaxPrefixLength));
            foreach (char ch in raw.Trim())
            {
                if (ch == '/' || ch == '\\') continue;

                bool rejected = false;
                for (int i = 0; i < invalid.Length; i++)
                {
                    if (invalid[i] == ch)
                    {
                        rejected = true;
                        break;
                    }
                }
                if (rejected) continue;

                builder.Append(ch);
                if (builder.Length >= MaxPrefixLength) break;
            }

            string result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? DefaultPrefix : result;
        }

        /// <summary>
        /// Insert a prefix before the final file-name segment.
        /// Example: PFView/Ships/X15/X15Albedo -> PFView/Ships/X15/MIN_X15Albedo
        /// </summary>
        internal static string ApplyPrefix(string relativePath, string prefix)
        {
            if (string.IsNullOrEmpty(relativePath)) return relativePath;

            string normalized = NormalizePrefix(prefix);
            int slash = relativePath.LastIndexOfAny(PathSeparators);
            if (slash < 0) return normalized + relativePath;

            return string.Concat(
                relativePath.AsSpan(0, slash + 1),
                normalized.AsSpan(),
                relativePath.AsSpan(slash + 1));
        }

        internal static string ApplyPrefix(string relativePath)
            => ApplyPrefix(relativePath, Prefix);
    }
}
