using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Newtonsoft.Json;

namespace GenerateCSharpErrors
{
    class Program
    {
        static void Main(string[] args)
        {
            var (options, exitCode) = CommandLineOptions.Parse(args);

            if (exitCode.HasValue)
            {
                Environment.Exit(exitCode.Value);
                return;
            }

            var errorCodes = GetErrorCodes(options);
            
            using (var writer = GetOutputWriter(options))
            {
                if (options.JsonOutput)
                {
                    WriteJson(errorCodes, writer);
                }
                else
                {
                    WriteMarkdownTable(errorCodes, writer, options);
                }
            }
        }

        const string ErrorCodesUrl = "https://raw.githubusercontent.com/dotnet/roslyn/master/src/Compilers/CSharp/Portable/Errors/ErrorCode.cs";
        const string ErrorFactsUrl = "https://raw.githubusercontent.com/dotnet/roslyn/master/src/Compilers/CSharp/Portable/Errors/ErrorFacts.cs";
        const string ErrorResourcesUrl = "https://raw.githubusercontent.com/dotnet/roslyn/master/src/Compilers/CSharp/Portable/CSharpResources.resx";
        const string DocUrlTemplate = "https://docs.microsoft.com/en-us/dotnet/articles/csharp/language-reference/compiler-messages/cs{0:D4}";
        const string DocLangReferenceUrl = "https://raw.githubusercontent.com/dotnet/docs/master/docs/csharp/language-reference/compiler-messages";

        private static IReadOnlyList<ErrorCode> GetErrorCodes(CommandLineOptions options)
        {
            using (var client = new HttpClient())
            {
                var enumMembers = GetErrorCodeEnumMembers(client);
                var warningLevels = GetWarningLevels(client);
                var messages = GetResourceDictionary(client);
                var docLinks = GetDocumentationLinks(client, options);
                var docDetails = GetDocDetails(client, docLinks.Keys, options);
                
                string GetMessage(string name) => messages.GetValueOrDefault(name);
                int GetWarningLevel(string name) => warningLevels.GetValueOrDefault(name);
                string GetDocLink(int value) => docLinks.TryGetValue(value, out var link) ? link : "";
                string GetDetails(int value) => docDetails.TryGetValue(value, out var link) ? link : "";

                var errorCodes =
                    enumMembers
                        .Select(m => ErrorCode.Create(m, GetMessage, GetWarningLevel, GetDocLink, GetDetails))
                        .ToList();

                return errorCodes;
            }
        }

        private static IEnumerable<EnumMemberDeclarationSyntax> GetErrorCodeEnumMembers(HttpClient client)
        {
            string errorCodesFileContent = client.GetStringAsync(ErrorCodesUrl).Result;
            var syntaxTree = CSharpSyntaxTree.ParseText(errorCodesFileContent);
            var root = syntaxTree.GetRoot();
            var enumDeclaration =
                root.DescendantNodes()
                    .OfType<EnumDeclarationSyntax>()
                    .First(e => e.Identifier.ValueText == "ErrorCode");
            return enumDeclaration.Members;
        }

        private static IReadOnlyDictionary<string, int> GetWarningLevels(HttpClient client)
        {
            var levels = new Dictionary<string, int>();
            string errorFactsFileContent = client.GetStringAsync(ErrorFactsUrl).Result;
            var syntaxTree = CSharpSyntaxTree.ParseText(errorFactsFileContent);
            var root = syntaxTree.GetRoot();
            var enumDeclaration =
                root.DescendantNodes()
                    .OfType<MethodDeclarationSyntax>()
                    .First(e => e.Identifier.ValueText == "GetWarningLevel");
            var sections = enumDeclaration.Body.DescendantNodes().OfType<SwitchStatementSyntax>().First().Sections;
            foreach(var section in sections)
            {
                var returnStatement = section.DescendantNodes().OfType<ReturnStatementSyntax>().First();
                var returnToken = returnStatement.Expression.GetFirstToken();
                if (returnToken.Kind() == SyntaxKind.NumericLiteralToken)
                {
                    foreach (var label in section.Labels)
                    {
                        levels[label.ColonToken.GetPreviousToken().ValueText] = (int)returnToken.Value;
                    }
                }
            }
            return levels;
        }

        private static IReadOnlyDictionary<string, string> GetResourceDictionary(HttpClient client)
        {
            string resourcesFileContent = client.GetStringAsync(ErrorResourcesUrl).Result;
            var doc = XDocument.Parse(resourcesFileContent);
            var dictionary =
                doc.Root.Elements("data")
                    .ToDictionary(
                        e => e.Attribute("name").Value,
                        e => e.Element("value").Value);
            return dictionary;
        }

        private static IReadOnlyDictionary<int, string> GetDocumentationLinks(HttpClient client, CommandLineOptions options)
        {
            var links = new Dictionary<int, string>();
            if (!options.IncludeLinks)
                return links;

            string toc = client.GetStringAsync($"{DocLangReferenceUrl}/toc.yml").Result;
            var regex = new Regex(@"href: cs(?<value>\d{4}).md", RegexOptions.IgnoreCase);
            var matches = regex.Matches(toc);
            foreach (Match m in matches)
            {
                int value = int.Parse(m.Groups["value"].Value);
                var url = string.Format(DocUrlTemplate, value);
                links.Add(value, url);
            }

            return links;
        }

        private static IReadOnlyDictionary<int, string> GetDocDetails(HttpClient client, IEnumerable<int> detailIds, CommandLineOptions options)
        {
            var details = new Dictionary<int, string>();
            if (!options.IncludeDetails)
                return details;

            foreach(int id in detailIds)
            {
                string doc = client.GetStringAsync($"{DocLangReferenceUrl}/cs{id:D4}.md").Result;
                // Skip Preamble, H1 and any preceding new line
                doc = string.Join("\n", doc.Split('\n').SkipWhile(l => !l.StartsWith("# ")).Skip(1).SkipWhile(string.IsNullOrWhiteSpace));
                details[id] = doc;
            }
            
            return details;
        }

        private static TextWriter GetOutputWriter(CommandLineOptions options)
        {
            if (string.IsNullOrWhiteSpace(options.Output))
            {
                return Console.Out;
            }
            else
            {
                var stream = File.Open(options.Output, FileMode.Create, FileAccess.Write);
                return new StreamWriter(stream, Encoding.UTF8);
            }
        }

        private static void WriteJson(IReadOnlyList<ErrorCode> errorCodes, TextWriter writer)
        {
            new JsonSerializer { Formatting = Formatting.Indented }.Serialize(writer, errorCodes.Select(error => new {
                id = error.Code,
                message = error.Message,
                description = error.Details,
                category = $"Level {error.WarningLevel}",
                severity = error.Severity.ToString(),
                type = error.Name,
                link = error.Link,
            }));
        }

        private static void WriteMarkdownTable(IReadOnlyList<ErrorCode> errorCodes, TextWriter writer, CommandLineOptions options)
        {
            writer.WriteLine("# All C# errors and warnings");
            
            writer.WriteLine();
            writer.WriteLine("*Parsed from the [Roslyn source code](https://github.com/dotnet/roslyn) using Roslyn.*");
            writer.WriteLine();
            
            string Link(ErrorCode e) =>
                string.IsNullOrEmpty(e.Link)
                    ? e.Code
                    : $"[{e.Code}]({e.Link})";

            if (options.IncludeDetails)
            {
                writer.WriteLine("|Code|Severity|Message|Details|");
                writer.WriteLine("|----|--------|-------|-------|");
            }
            else
            {
                writer.WriteLine("|Code|Severity|Message|");
                writer.WriteLine("|----|--------|-------|");
            }
            foreach (var e in errorCodes)
            {
                if (e.Severity== Severity.Unknown) continue;
                writer.Write($"|{Link(e)}|{e.Severity}|{e.Message}|");
                if (options.IncludeDetails) {
                    writer.Write($"{e.Details}|".Replace("\n", "<br>"));
                }
                writer.WriteLine();
            }

            writer.WriteLine();
            writer.WriteLine("## Statistics");
            writer.WriteLine();

            var lookup = errorCodes.OrderByDescending(e => e.Severity).ToLookup(e => e.Severity);
            writer.WriteLine("|Severity|Count|");
            writer.WriteLine("|--------|-----|");
            foreach (var g in lookup)
            {
                if (g.Key == Severity.Unknown) continue;
                writer.WriteLine($"|{g.Key}|{g.Count()}|");
            }
            writer.WriteLine($"|**Total**|**{errorCodes.Count}**|");
        }

        class ErrorCode
        {
            public static ErrorCode Create(
                EnumMemberDeclarationSyntax member,
                Func<string, string> getMessageByName,
                Func<string, int> getWarningLevel,
                Func<int, string> getLinkByValue,
                Func<int, string> getDetailsByValue)
            {
                string name = member.Identifier.ValueText;
                if (name == "Void" || name == "Unknown")
                {
                    return new ErrorCode(name, 0, Severity.Unknown, "", 0, "", "");
                }
                else
                {
                    int value = int.Parse(member.EqualsValue?.Value?.GetText()?.ToString() ?? "0");
                    return new ErrorCode(
                        name.Substring(4),
                        value,
                        ParseSeverity(name.Substring(0, 3)),
                        getMessageByName(name + "_Title") ?? getMessageByName(name) ?? "",
                        getWarningLevel(name),
                        getLinkByValue(value),
                        getDetailsByValue(value) ?? getMessageByName(name + "_Description"));
                }
            }
            
            private ErrorCode(string name, int value, Severity severity, string message, int warningLevel, string link, string details)
            {
                Name = name;
                Value = value;
                Severity = severity;
                Message = message;
                WarningLevel = warningLevel;
                Link = link;
                Details = details;
            }
            
            public string Name { get; }
            public int Value { get; }
            public string Code => $"CS{Value:D4}";
            public Severity Severity { get; }
            public string Message { get; }
            public string Link { get; }
            public string Details { get; set; }
            public int WarningLevel { get; set; }

            private static Severity ParseSeverity(string severity)
            {
                switch (severity)
                {
                    case "HDN":
                        return Severity.Hidden;
                    case "INF":
                        return Severity.Info;
                    case "WRN":
                        return Severity.Warning;
                    case "ERR":
                        return Severity.Error;
                    case "FTL":
                        return Severity.Fatal;
                    default:
                        return Severity.Unknown;
                }
            }
        }

        enum Severity
        {
            Unknown,
            Hidden,
            Info,
            Warning,
            Error,
            Fatal
        }

        class CommandLineOptions
        {
            public string Output { get; set; }
            
            public bool JsonOutput { get; set; }
            
            public bool IncludeLinks { get; set; }

            public bool IncludeDetails { get; set; }

            private static readonly IImmutableSet<string> _helpOptions =
                ImmutableHashSet.Create(
                    StringComparer.OrdinalIgnoreCase,
                    "-h", "-?", "--help");
            private static readonly IImmutableSet<string> _outputOptions =
                ImmutableHashSet.Create(
                    StringComparer.OrdinalIgnoreCase,
                    "-o", "--output");
            private static readonly IImmutableSet<string> _linksOptions =
                ImmutableHashSet.Create(
                    StringComparer.OrdinalIgnoreCase,
                    "-l", "--link");
            private static readonly IImmutableSet<string> _detailsOptions =
                ImmutableHashSet.Create(
                    StringComparer.OrdinalIgnoreCase,
                    "-d", "--details");
            private static readonly IImmutableSet<string> _jsonOptions =
                ImmutableHashSet.Create(
                    StringComparer.OrdinalIgnoreCase,
                    "-j", "--json");
            public static (CommandLineOptions options, int? exitCode) Parse(string[] args)
            {
                var options = new CommandLineOptions();

                for (int i = 0; i < args.Length; i++)
                {
                    var option = args[i];

                    if (_helpOptions.Contains(option))
                    {
                        ShowUsage();
                        return (options, 0);
                    }
                    else if (_outputOptions.Contains(option))
                    {
                        if (i + 1 >= args.Length)
                        {
                            ShowUsage($"Missing filename for {option} option");
                            return (options, 1);
                        }
                        options.Output = args[++i];
                    }
                    else if (_linksOptions.Contains(option))
                    {
                        options.IncludeLinks = true;
                    }
                    else if (_jsonOptions.Contains(option))
                    {
                        options.JsonOutput = true;
                    }
                    else if (_detailsOptions.Contains(option))
                    {
                        options.IncludeDetails = true;
                    }
                    else
                    {
                        ShowUsage($"Unknown option: {option}");
                        return (options, 1);
                    }
                }
                
                return (options, null);
            }

            private static void ShowUsage(string error = null)
            {
                if (!string.IsNullOrEmpty(error))
                {
                    var normalColor = Console.ForegroundColor;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine(error);
                    Console.ForegroundColor = normalColor;
                    Console.WriteLine();
                }

                Console.WriteLine("C# errors and warnings list generator");
                Console.WriteLine();
                Console.WriteLine("Usage: GenerateCSharpErrors.exe [options]");
                Console.WriteLine();
                Console.WriteLine("Options:");
                Console.WriteLine("  -h|--help              Show this help message");
                Console.WriteLine("  -o|--output <file>     Output to the specified file (default: output to the console)");
                Console.WriteLine("  -l|--link              Include links to documentation when they exist");
                Console.WriteLine("  -d|--details           Gather documentation in markdown format");
                Console.WriteLine("  -j|--json              Write output in JSON format");
                Console.WriteLine();
            }
        }
    }
}
