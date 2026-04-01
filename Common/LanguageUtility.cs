using System.Text.RegularExpressions;
using TinyPinyin;
using System;
using System.IO;

namespace MediaInfoKeeper.Common
{
    internal static class LanguageUtility
    {
        private static readonly Regex ChineseRegex = new Regex(@"[\u4E00-\u9FFF]", RegexOptions.Compiled);
        private static readonly Regex JapaneseRegex = new Regex(@"[\u3040-\u30FF]", RegexOptions.Compiled);
        private static readonly Regex DefaultChineseCollectionNameRegex = new Regex(@"（系列）$", RegexOptions.Compiled);

        public static bool IsChinese(string input)
        {
            return !string.IsNullOrEmpty(input) &&
                   ChineseRegex.IsMatch(input) &&
                   !JapaneseRegex.IsMatch(input.Replace("\u30FB", string.Empty));
        }

        public static string ConvertToPinyinInitials(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return input;
            }

            try
            {
                return PinyinHelper.GetPinyinInitials(input);
            }
            catch (Exception ex) when (
                ex is FileNotFoundException ||
                ex is FileLoadException ||
                ex is TypeLoadException)
            {
                return input;
            }
        }

        public static string RemoveDefaultCollectionName(string input)
        {
            return string.IsNullOrEmpty(input)
                ? input
                : DefaultChineseCollectionNameRegex.Replace(input, string.Empty).Trim();
        }
    }
}
