using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace Azure.Sdk.Tools.CodeOwnersParser
{
    /// <summary>
    /// Represents a CODEOWNERS file entry that matched to targetPath from
    /// the list of entries, assumed to have been parsed from CODEOWNERS file.
    ///
    /// To use this class, construct it, passing as input relevant paths.
    /// Then, to obtain the value of the matched entry, reference "Value" member.
    ///
    /// This class uses a regex-based wildcard-supporting (* and **) matcher.
    ///
    /// This matcher reflects the matching behavior of the built-in GitHub CODEOWNERS interpreter,
    /// but with additional assumptions imposed about the paths present in CODEOWNERS, as guaranteed
    /// by CODEOWNERS file validation:
    /// https://github.com/Azure/azure-sdk-tools/issues/4859
    /// These assumptions are checked by IsCodeownersPathValid() method.
    /// If violated, given CODEOWNERS path will be always ignored by the matcher, never matching anything.
    /// As a result, this matcher is effectively a subset of GitHub CODEOWNERS matcher.
    ///
    /// The validation spec is given in this comment:
    /// https://github.com/Azure/azure-sdk-tools/issues/4859#issuecomment-1370360622
    /// See also RetrieveCodeOwnersProgramTests and CodeownersFileTests tests.
    ///
    /// Reference:
    /// https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/about-code-owners#codeowners-syntax
    /// https://git-scm.com/docs/gitignore#_pattern_format
    /// </summary>
    public class MatchedCodeownersEntry
    {
        /// <summary>
        /// Token for temporarily substituting "**" in regex, to avoid it being escaped when
        /// Regex.Escape is called.
        /// </summary>
        private const string DoubleStar = "_DOUBLE_STAR_";
        /// <summary>
        /// Token for temporarily substituting "*" in regex, to avoid it being escaped when
        /// Regex.Escape is called.
        /// </summary>
        private const string SingleStar = "_SINGLE_STAR_";

        public readonly CodeownersEntry Value;

        /// <summary>
        /// See comment on IsCodeownersPathValid
        /// </summary>
        public bool IsValid => IsCodeownersPathValid(this.Value.PathExpression);

        /// <summary>
        /// See the class comment to understand this method purpose.
        /// </summary>
        public static bool IsCodeownersPathValid(string codeownersPathExpression)
            => !IsCommentedOutPath(codeownersPathExpression)
               && !ContainsUnsupportedCharacters(codeownersPathExpression) 
               && !ContainsUnsupportedSequences(codeownersPathExpression);

        /// <summary>
        /// Any CODEOWNERS path with these characters will be skipped.
        /// Note these are valid parts of file paths, but we are not supporting
        /// them to simplify the matcher logic.
        /// </summary>
        private static readonly char[] unsupportedChars = { '[', ']', '!', '?' };

        public MatchedCodeownersEntry(string targetPath, List<CodeownersEntry> codeownersEntries)
        {
            this.Value = GetMatchingCodeownersEntry(targetPath, codeownersEntries);
        }

        /// <summary>
        /// Returns a CodeownersEntry from codeownersEntries, after normalization and validation,
        /// that match targetPath per algorithm described in the GitHub CODEOWNERS reference,
        /// as linked to in this class comment.
        ///
        /// Paths that are not valid after normalization are skipped from matching.
        ///
        /// If there is no match, this method returns "new CodeownersEntry()".
        /// 
        /// For definition of "normalization", see NormalizePath().
        /// For definition of "validation", see IsCodeownersPathValid().
        /// You can also refer to the validation spec linked from this class comment.
        /// </summary>
        private CodeownersEntry GetMatchingCodeownersEntry(
            string targetPath,
            List<CodeownersEntry> codeownersEntries)
        {
            if (targetPath.Contains('*'))
            {
                Console.Error.WriteLine(
                    $"Target path \"{targetPath}\" contains star ('*') which is not supported. " 
                    + "Returning no match without checking for ownership.");
                return NoMatchCodeownersEntry;
            }

            // targetPath is assumed to be absolute w.r.t. repository root, hence we ensure
            // it starts with "/" to denote that.
            if (!targetPath.StartsWith("/"))
                targetPath = "/" + targetPath;

            // Note we cannot add or trim the slash at the end of targetPath.
            // Slash at the end of target path denotes it is a directory, not a file,
            // so it can not match against a CODEOWNERS entry that is guaranteed to be a file,
            // by the virtue of not ending with "/".
            
            CodeownersEntry matchedEntry = codeownersEntries
                .Where(entry => IsCodeownersPathValid(entry.PathExpression))
                // Entries listed in CODEOWNERS file below take precedence, hence we read the file from the bottom up.
                // By convention, entries in CODEOWNERS should be sorted top-down in the order of:
                // - 'RepoPath',
                // - 'ServicePath'
                // - and then 'PackagePath'.
                // However, due to lack of validation, as of 12/29/2022 this is not always the case.
                .Reverse()
                .FirstOrDefault(
                    entry => Matches(targetPath, entry), 
                    // assert: none of the codeownersEntries matched targetPath
                    NoMatchCodeownersEntry);

            return matchedEntry;
        }

        private CodeownersEntry NoMatchCodeownersEntry { get; } = new CodeownersEntry();

        /// <summary>
        /// We do not output any error message in case the path is commented out,
        /// as a) such paths are expected to be processed and discarded by this logic
        /// and b) outputting error messages would possibly result in output
        /// truncation, truncating the resulting json, and thus making the output of the
        /// calling tool malformed.
        /// </summary>
        private static bool IsCommentedOutPath(string codeownersPathExpression)
            => codeownersPathExpression.Trim().StartsWith("#");

        /// <summary>
        /// See the comment on unsupportedChars.
        /// </summary>
        private static bool ContainsUnsupportedCharacters(string codeownersPath)
        {
            var contains = unsupportedChars.Any(codeownersPath.Contains);
            if (contains)
            {
                Console.Error.WriteLine(
                    $"CODEOWNERS path \"{codeownersPath}\" contains unsupported characters: " +
                    string.Join(' ', unsupportedChars) +
                    " Because of that this path will never match.");
            }
            return contains;
        }

        private static bool ContainsUnsupportedSequences(string codeownersPath)
        {
            if (codeownersPath == "/")
            {
                // This behavior matches GitHub CODEOWNERS interpreter behavior.
                // I.e. a path of just "/" is unsupported.
                Console.Error.WriteLine(
                    $"CODEOWNERS path \"{codeownersPath}\" will never match. " +
                    "Use \"/**\" instead.");
                return true;
            }

            // See the comment below on why we support this path.
            if (codeownersPath == "/**")
                return false;

            if (!codeownersPath.StartsWith("/"))
            {
                Console.Error.WriteLine(
                    $"CODEOWNERS path \"{codeownersPath}\" does not start with " +
                    "\"/\". Prefix it with \"/\". " +
                    "Until then this path will never match.");
                return true;
            }

            // We do not support suffix of "/**" because it is equivalent to "/".
            // For example, "/foo/**" is equivalent to "/foo/"
            // One exception to this rule is if the entire path is "/**":
            // GitHub doesn't match "/" to anything if it is the entire path,
            // and instead expects "/**".
            if (codeownersPath != "/**" && codeownersPath.EndsWith("/**"))
            {
                Console.Error.WriteLine(
                    $"CODEOWNERS path \"{codeownersPath}\" ends with " +
                    "unsupported sequence of \"/**\". Replace it with \"/\". " +
                    "Until then this path will never match.");
                return true;
            }

            // We do not support suffix of "/**/" because it is equivalent to
            // suffix "/**" which is equivalent to suffix "/".
            if (codeownersPath.EndsWith("/**/"))
            {
                Console.Error.WriteLine(
                    $"CODEOWNERS path \"{codeownersPath}\" ends with " +
                    "unsupported sequence of \"/**/\". Replace it with \"/\". " +
                    "Until then this path will never match.");
                return true;
            }

            // We do not support inline "**", i.e. if it is not within slashes, i.e. "/**/".
            // Any inline "**" like "/a**/" or "/**a/" or "/a**b/"
            // would be equivalent to single star, hence we forbid double star, to avoid confusion.
            if (codeownersPath.Replace("/**/", "").Contains("**"))
            {
                Console.Error.WriteLine(
                    $"CODEOWNERS path \"{codeownersPath}\" contains " +
                    "unsupported sequence of \"**\". Double star can be used only within slashes \"/**/\" " +
                    "or as a top-level catch all path of \"/**\". "  +
                    "Currently this path will never match.");
                return true;
            }

            return false;
        }

        /// <summary>
        /// Returns true if the regex expression representing the PathExpression
        /// of CODEOWNERS entry matches a prefix of targetPath.
        /// </summary>
        private bool Matches(string targetPath, CodeownersEntry entry)
        {
            string codeownersPath = entry.PathExpression;

            Regex regex = ConvertToRegex(codeownersPath);
            // Is prefix match. I.e. it will return true if the regex matches
            // a prefix of targetPath.
            return regex.IsMatch(targetPath);
        }

        private Regex ConvertToRegex(string codeownersPath)
        {
            Trace.Assert(IsCodeownersPathValid(codeownersPath));

            // Special case: path "/**" matches everything.
            // We do not allow "/**" in any other context except when it is the entire path.
            if (codeownersPath == "/**")
                return new Regex(".*");

            string pattern = codeownersPath;

            if (codeownersPath.Contains(SingleStar) || pattern.Contains(DoubleStar))
            {
                Console.Error.WriteLine(
                    $"CODEOWNERS path \"{codeownersPath}\" contains reserved phrases: " +
                    $"\"{DoubleStar}\" or \"{SingleStar}\"");
            }

            // We replace "/**/", not "**", because we disallow "**" in any other context.
            // Specifically:
            // - because we normalize the path to start with "/", any prefix "**/" is
            // effectively "/**/";
            // - any suffix "/**", for reasons explained within ContainsUnsupportedSequences().
            // - any inline "**", for reasons explained within ContainsUnsupportedSequences().
            pattern = pattern.Replace("/**/", "/" + DoubleStar + "/");
            pattern = pattern.Replace("*", SingleStar);

            pattern = Regex.Escape(pattern);

            // Denote that all paths are absolute by pre-pending "beginning of string" symbol.
            pattern = "^" + pattern;

            pattern = SetPatternSuffix(pattern);

            pattern = pattern.Replace($"/{DoubleStar}/", "((/.*/)|/)");
            pattern = pattern.Replace(SingleStar, "([^/]*)");

            return new Regex(pattern);
        }

        /// <summary>
        /// Sets the regex pattern suffix, which can be either "$" (end of string)
        /// or nothing.
        ///
        /// GitHub's CODEOWNERS matching logic is a bit inconsistent when it comes to handling suffixes.
        ///
        /// In a nutshell:
        /// - For top level dir, `/` doesn't work. One has to use `/**`.
        /// - But for nested dirs, `/` works. I.e. one can write `/foo/` and it is equivalent to `/foo/**`.
        /// - `*` has different interpretations. If used with preceding slash, like `/*` or `/foo/*`
        /// it means "things only in this dir". But when used as a suffix, it means "anything".
        /// So `/foo*` is effectively `/foo*/** OR /foo*`.  Where `*` in the OR clause
        /// should be interpreted as "any character except `/`".
        /// </summary>
        private static string SetPatternSuffix(string pattern)
        {
            // If a pattern ends with "/*" this means it should match only files
            // in the child directory, but not all descendant directories.
            // Hence we must append "$", to avoid treating the regex pattern
            // as a prefix match and instead treat it as an exact match.
            if (pattern.EndsWith($"/{SingleStar}"))
                return pattern + "$";

            Trace.Assert(pattern != "^/", "Path \"/\" should have been excluded by validation.");

            // If the pattern ends with "/" it means it is a path to a directory,
            // like "/foo/". This means "match everything in this directory,
            // at arbitrary directory nesting depth."
            //
            // If the pattern ends with "*" but not "/*" (as this case was handled above)
            // then it is a suffix *, e.g. "/foo*". This means "match everything
            // with a prefix string of "/foo". Notably, it matches not only
            // everything in "/foo/" dir, but also files like "/foobar.txt"
            if (pattern.EndsWith("/") || pattern.EndsWith(SingleStar))
                return pattern;

            // If the pattern doesn't end with "/" nor "*", then according to GitHub CODEOWNERS
            // matcher it is a path to a file, or to a directory with exact match.
            // However, in this matcher we assume stricter interpretation where 
            // it has to be a path to a file.
            //
            // As a result we append "$", to avoid treating the regex pattern
            // as a prefix match and instead treat it as an exact match.
            //
            // If that assumption is violated, i.e. the CODEOWNERS path is actually
            // a path to a directory, due to appending "$" the result will be no match,
            // e.g. targetPath of "/foo/bar" is a no match against "/foo$".
            // This is the desired behavior, as we don't want to match against invalid paths.
            return pattern + "$";
        }
    }
}
