using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace DDD.Analyzers.Tests.TestingInfrastructure.Analyzer;

internal static class DiagnosticAnalyzerTestExtensions
{
    private const string DefaultFilePathPrefix = "Test";

    private const string CSharpDefaultFileExt = "cs";

    private const string TestProjectName = "TestProject";

    private static readonly MetadataReference CoreLibraryReference =
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location);

    private static readonly MetadataReference SystemCoreReference =
        MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location);

    private static readonly MetadataReference CSharpSymbolsReference =
        MetadataReference.CreateFromFile(typeof(CSharpCompilation).Assembly.Location);

    private static readonly MetadataReference CodeAnalysisReference =
        MetadataReference.CreateFromFile(typeof(Compilation).Assembly.Location);

    private static readonly MetadataReference SystemDiagnosticReference =
        MetadataReference.CreateFromFile(typeof(Process).Assembly.Location);

    public static Task<TResult[]> WhenAllAsync<TResult>(this IEnumerable<Task<TResult>> tasks)
    {
        if (tasks is Task<TResult>[] taskArray)
        {
            return Task.WhenAll(taskArray);
        }

        return Task.WhenAll(tasks.ToArray());
    }

    #region [Get Diagnostics]

    /// <summary>
    /// Данные классы в виде строк, их язык и применяемый к нему DiagnosticAnalyzer
    /// возвращают диагностику, найденную в строке, после преобразования ее в документ.
    /// </summary>
    /// <param name="sources"> Классы в виде строк </param>
    /// <param name="language"> Язык, на котором находятся исходные классы </param>
    /// <param name="analyzer"> Анализатор для запуска на исходниках </param>
    /// <param name="references">
    /// Дополнительные сборки, которые будут использоваться при компиляции исходных
    /// классов.
    /// </param>
    /// <returns>
    /// Диагностика, найденная в исходных классах, после преобразования их в документ.
    /// </returns>
    public static Task<Diagnostic[]> GetSortedDiagnosticsAsync(this DiagnosticAnalyzer analyzer,
        string[] sources,
        string language, IEnumerable<MetadataReference> references = null) =>
        GetSortedDiagnosticsFromDocumentsAsync(analyzer, GetDocuments(sources, language, references));

    /// <summary>
    /// Имея анализатор и документ, к которому его нужно применить, запустите
    /// анализатор и соберите массив обнаруженных в нем диагностик.
    /// Возвращённые диагностические данные затем упорядочиваются по расположению в
    /// исходном документе.
    /// </summary>
    /// <param name="analyzer"> Анализатор для работы с документами </param>
    /// <param name="documents"> Документы, на которых будет работать анализатор </param>
    /// <returns>
    /// Диагностика, найденная в исходных классах, после преобразования их в документ.
    /// </returns>
    public static async Task<Diagnostic[]> GetSortedDiagnosticsFromDocumentsAsync(this DiagnosticAnalyzer analyzer,
        Document[] documents)
    {
        var compilations = await documents.Select(document => document.Project.GetCompilationAsync())
            .WhenAllAsync();

        var diagnosticsByProject = await compilations.Select(compilation => compilation
                .WithAnalyzers(ImmutableArray.Create(analyzer))
                .GetAnalyzerDiagnosticsAsync())
            .WhenAllAsync();

        var diagnostics = new List<Diagnostic>();

        var syntaxTrees = await documents.Select(document => document.GetSyntaxTreeAsync())
            .WhenAllAsync();

        foreach (var diagnostic in diagnosticsByProject.SelectMany(diagnostic => diagnostic))
        {
            if (diagnostic.Location == Location.None || diagnostic.Location.IsInMetadata)
            {
                diagnostics.Add(diagnostic);
            }
            else if (syntaxTrees.Any(x => x == diagnostic.Location.SourceTree))
            {
                diagnostics.Add(diagnostic);
            }
        }

        return SortDiagnostics(diagnostics);
    }

    /// <summary>
    /// Сортировка диагностики по расположению в исходном документе
    /// </summary>
    /// <param name="diagnostics"> Список диагностик для сортировки </param>
    /// <returns>
    /// Отсортированный список диагностик
    /// </returns>
    private static Diagnostic[] SortDiagnostics(IEnumerable<Diagnostic> diagnostics) => diagnostics
        .OrderBy(d => d.Location.SourceSpan.Start)
        .ToArray();

    #endregion

    #region [Set up compilation and documents]

    /// <summary>
    /// Учитывая массив строк в качестве источников и язык, превратите их в проект и
    /// верните его документы и диапазоны.
    /// </summary>
    /// <param name="sources"> Классы в виде строк </param>
    /// <param name="language"> Язык исходного кода </param>
    /// <param name="references">
    /// Дополнительные сборки, которые будут использоваться при компиляции исходных
    /// классов.
    /// </param>
    /// <returns>
    /// Кортеж, содержащий документы, созданные из источников, и их TextSpans, если это
    /// необходимо.
    /// </returns>
    private static Document[] GetDocuments(IReadOnlyCollection<string> sources, string language,
        IEnumerable<MetadataReference> references = null)
    {
        if (language != LanguageNames.CSharp && language != LanguageNames.VisualBasic)
        {
            throw new ArgumentException("Неподдерживаемый язык");
        }

        var project = CreateProject(sources, language, references);
        var documents = project.Documents.ToArray();

        if (sources.Count != documents.Length)
        {
            throw new SystemException("Количество источников не соответствует количеству созданных документов");
        }

        return documents;
    }

    /// <summary>
    /// Создайте документ из строки, создав содержащий ее проект.
    /// </summary>
    /// <param name="source"> Классы в виде строки </param>
    /// <param name="language"> Язык исходного кода </param>
    /// <param name="references">
    /// Дополнительные сборки, которые будут использоваться при компиляции исходных
    /// классов.
    /// </param>
    /// <returns> Документ, созданный из исходной строки </returns>
    public static Document CreateDocument(string source, string language,
        IEnumerable<MetadataReference> references = null) =>
        CreateProject(new[]
            {
                source
            }, language, references)
            .Documents.First();

    /// <summary>
    /// Создайте проект, используя введенные строки в качестве источников.
    /// </summary>
    /// <param name="sources"> Классы в виде строк </param>
    /// <param name="language"> Язык исходного кода </param>
    /// <param name="references">
    /// Дополнительные сборки, которые будут использоваться при компиляции исходных
    /// классов.
    /// </param>
    /// <returns>
    /// Проект, созданный из документов, созданных из исходных строк
    /// </returns>
    private static Project CreateProject(IEnumerable<string> sources, string language,
        IEnumerable<MetadataReference> references = null)
    {
        var fileExt = language == LanguageNames.CSharp
            ? CSharpDefaultFileExt
            : throw new NotImplementedException($"Поддержка языка {language} не реализована");

        var projectId = ProjectId.CreateNewId(TestProjectName);

        var solution = new AdhocWorkspace()
            .CurrentSolution
            .AddProject(projectId, TestProjectName, TestProjectName, language)
            .AddMetadataReference(projectId, CoreLibraryReference)
            .AddMetadataReference(projectId, SystemCoreReference)
            .AddMetadataReference(projectId, CSharpSymbolsReference)
            .AddMetadataReference(projectId, CodeAnalysisReference)
            .AddMetadataReference(projectId, SystemDiagnosticReference);

        if (references != null)
        {
            solution = solution.AddMetadataReferences(projectId, references);
        }

        var count = 0;

        foreach (var source in sources)
        {
            var newFileName = DefaultFilePathPrefix + count + "." + fileExt;
            var documentId = DocumentId.CreateNewId(projectId, newFileName);
            solution = solution.AddDocument(documentId, newFileName, SourceText.From(source));
            count++;
        }

        return solution.GetProject(projectId);
    }

    #endregion

    #region [Actual comparisons and verifications]

    /// <summary>
    /// Проверяет каждую из найденных фактических диагностики и сравнивает их с
    /// соответствующим DiagnosticResult в массиве ожидаемых результатов.
    /// Диагностика считается одинаковой только в том случае, если
    /// DiagnosticResultLocation, Id, Severity и Message DiagnosticResult соответствуют
    /// фактической диагностике.
    /// </summary>
    /// <param name="actualResults">
    /// Диагностика, найденная компилятором после запуска анализатора исходного кода
    /// </param>
    /// <param name="analyzer"> Анализатор, который запускался на исходниках </param>
    /// <param name="expectedResults">
    /// Результаты диагностики, которые должны были появиться в коде
    /// </param>
    public static VerifyDiagnosticAnalyzerResult VerifyDiagnosticResults(this DiagnosticAnalyzer analyzer,
        IEnumerable<Diagnostic> actualResults,
        DiagnosticResult[] expectedResults)
    {
        var expectedCount = expectedResults.Length;
        var diagnostics = actualResults as Diagnostic[] ?? actualResults.ToArray();
        var actualCount = diagnostics.Length;

        if (expectedCount != actualCount)
        {
            var diagnosticsOutput = diagnostics.Any()
                ? FormatDiagnostics(analyzer, diagnostics.ToArray())
                : "    NONE.";

            var msg = GetMismatchNumberOfDiagnosticsMessage(expectedCount, actualCount, diagnosticsOutput);

            return VerifyDiagnosticAnalyzerResult.Fail(msg);
        }

        for (var i = 0; i < expectedResults.Length; i++)
        {
            var actual = diagnostics.ElementAt(i);
            var expected = expectedResults[i];

            if (expected is
                {
                    Line: -1,
                    Column: -1
                })
            {
                if (actual.Location != Location.None)
                {
                    var msg = GetExpectedDiagnosticWithNoLocation(analyzer, actual);

                    return VerifyDiagnosticAnalyzerResult.Fail(msg);
                }
            }
            else
            {
                var locationResult =
                    VerifyDiagnosticLocation(analyzer, actual, actual.Location, expected.Locations.First());

                if (!locationResult.Success)
                {
                    return locationResult;
                }

                var additionalLocations = actual.AdditionalLocations.ToArray();

                if (additionalLocations.Length != expected.Locations.Length - 1)
                {
                    var msg = GetNotExpectedLocation(analyzer, actual, expected, additionalLocations);

                    return VerifyDiagnosticAnalyzerResult.Fail(msg);
                }

                for (var j = 0; j < additionalLocations.Length; ++j)
                {
                    locationResult = VerifyDiagnosticLocation(analyzer,
                        actual,
                        additionalLocations[j],
                        expected.Locations[j + 1]);

                    if (!locationResult.Success)
                    {
                        return locationResult;
                    }
                }
            }

            if (actual.Id != expected.Id)
            {
                var msg = GetNoExpectedDiagnosticId(analyzer, actual, expected);

                return VerifyDiagnosticAnalyzerResult.Fail(msg);
            }

            if (actual.Severity != expected.Severity)
            {
                var msg = GetNotExpectedSeverityMessage(analyzer, actual, expected);

                return VerifyDiagnosticAnalyzerResult.Fail(msg);
            }

            if (actual.GetMessage() == expected.Message)
            {
                continue;
            }

            {
                var msg = GetNotExpectedMessage(analyzer, actual, expected);

                return VerifyDiagnosticAnalyzerResult.Fail(msg);
            }
        }

        return VerifyDiagnosticAnalyzerResult.Ok();
    }

    /// <summary>
    /// Вспомогательный метод для VerifyDiagnosticResult, который проверяет
    /// расположение диагностики и сравнивает ее с расположением в ожидаемом
    /// DiagnosticResult.
    /// </summary>
    /// <param name="analyzer"> Анализатор, который запускался на исходниках </param>
    /// <param name="diagnostic"> Диагностика, которая была найдена в коде </param>
    /// <param name="actual"> Расположение диагностики в коде </param>
    /// <param name="expected">
    /// Местоположение DiagnosticResultLocation, которое должно было быть найдено в
    /// коде
    /// </param>
    private static VerifyDiagnosticAnalyzerResult VerifyDiagnosticLocation(DiagnosticAnalyzer analyzer,
        Diagnostic diagnostic,
        Location actual,
        DiagnosticResultLocation expected)
    {
        var actualSpan = actual.GetLineSpan();

        var isInExpectedFile = actualSpan.Path == expected.Path
                               || (actualSpan.Path.Contains("Test0.")
                                   && expected.Path.Contains("Test."));

        if (!isInExpectedFile)
        {
            var msg = GetNotInExpectedFileMessage(analyzer, diagnostic, expected, actualSpan);

            return VerifyDiagnosticAnalyzerResult.Fail(msg);
        }

        var actualLinePosition = actualSpan.StartLinePosition;

        // Проверяйте положение линии только в том случае, если в реальной диагностике есть фактическая линия.
        if (actualLinePosition.Line > 0)
        {
            if (actualLinePosition.Line + 1 != expected.Line)
            {
                var msg = GetNotInExpectedLineMessage(analyzer, diagnostic, expected, actualLinePosition);

                return VerifyDiagnosticAnalyzerResult.Fail(msg);
            }
        }

        // Проверяйте положение столбца только в том случае, если в реальной диагностике есть фактическое положение столбца.
        if (actualLinePosition.Character <= 0 || actualLinePosition.Character + 1 == expected.Column)
        {
            return VerifyDiagnosticAnalyzerResult.Ok();
        }

        {
            var msg = GetNotInExpectedColumn(analyzer, diagnostic, expected, actualLinePosition);

            return VerifyDiagnosticAnalyzerResult.Fail(msg);
        }
    }

    private static string GetMismatchNumberOfDiagnosticsMessage(int expectedCount, int actualCount,
        string diagnosticsOutput)
    {
        var sb = new StringBuilder();

        sb.Append(
            $"Несоответствие между количеством возвращаемых диагностических данных, ожидается \"{expectedCount}\" но было \"{actualCount}\"");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Диагностики:");
        sb.AppendLine();
        sb.AppendLine(diagnosticsOutput);
        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetExpectedDiagnosticWithNoLocation(DiagnosticAnalyzer analyzer, Diagnostic actual)
    {
        var sb = new StringBuilder();

        sb.AppendLine("Ожидается:");
        sb.AppendLine("Диагностика проекта без местоположения");
        sb.AppendLine("Актуальное:");

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            actual
        }));

        return sb.ToString();
    }

    private static string GetNotExpectedLocation(DiagnosticAnalyzer analyzer,
        Diagnostic actual,
        DiagnosticResult expected,
        IReadOnlyCollection<Location> additionalLocations)
    {
        var sb = new StringBuilder();

        sb.Append(
            $"Ожидается {expected.Locations.Length - 1} дополнительных мест но есть {additionalLocations.Count} для диагностик:");

        sb.AppendLine();

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            actual
        }));

        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetNoExpectedDiagnosticId(DiagnosticAnalyzer analyzer,
        Diagnostic actual,
        DiagnosticResult expected)
    {
        var sb = new StringBuilder();

        sb.Append($"Ожидаемый диагностический идентификатор \"{expected.Id}\" был \"{actual.Id}\"");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Диагностики:");
        sb.AppendLine();

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            actual
        }));

        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetNotExpectedSeverityMessage(DiagnosticAnalyzer analyzer,
        Diagnostic actual,
        DiagnosticResult expected)
    {
        var sb = new StringBuilder();

        sb.Append($"Ожидаемый идентификатор диагностически \"{expected.Severity}\" был \"{actual.Severity}\"");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Диагностики:");
        sb.AppendLine();

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            actual
        }));

        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetNotExpectedMessage(DiagnosticAnalyzer analyzer,
        Diagnostic actual,
        DiagnosticResult expected)
    {
        var sb = new StringBuilder();

        sb.Append($"Ожидаемое сообщение диагностически \"{expected.Message}\" было \"{actual.GetMessage()}\"");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Диагностики:");
        sb.AppendLine();

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            actual
        }));

        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetNotInExpectedColumn(DiagnosticAnalyzer analyzer,
        Diagnostic diagnostic,
        DiagnosticResultLocation expected,
        LinePosition actualLinePosition)
    {
        var sb = new StringBuilder();

        sb.Append(
            $"Ожидаемое начало диагностики со столбца \"{expected.Column}\" на самом деле было со столбца \"{actualLinePosition.Character + 1}\"");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Диагностики:");
        sb.AppendLine();

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            diagnostic
        }));

        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetNotInExpectedLineMessage(DiagnosticAnalyzer analyzer,
        Diagnostic diagnostic,
        DiagnosticResultLocation expected,
        LinePosition actualLinePosition)
    {
        var sb = new StringBuilder();

        sb.Append(
            $"Ожидалось, что диагностика будет на строке \"{expected.Line}\", на самом деле была на строке \"{actualLinePosition.Line + 1}\"");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Диагностики:");
        sb.AppendLine();

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            diagnostic
        }));

        sb.AppendLine();

        return sb.ToString();
    }

    private static string GetNotInExpectedFileMessage(DiagnosticAnalyzer analyzer,
        Diagnostic diagnostic,
        DiagnosticResultLocation expected,
        FileLinePositionSpan actualSpan)
    {
        var sb = new StringBuilder();

        sb.Append(
            $"Ожидаемая диагностика в файле \"{expected.Path}\" на самом деле была в файле \"{actualSpan.Path}\"");

        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("Диагностики:");
        sb.AppendLine();

        sb.AppendLine(FormatDiagnostics(analyzer, new[]
        {
            diagnostic
        }));

        sb.AppendLine();

        return sb.ToString();
    }

    /// <summary>
    /// Вспомогательный метод для форматирования диагностики в легко читаемую строку
    /// </summary>
    /// <param name="analyzer"> Анализатор, который тестирует этот верификатор </param>
    /// <param name="diagnostics"> Диагностика для форматирования </param>
    /// <returns> Диагностика в виде строки </returns>
    private static string FormatDiagnostics(DiagnosticAnalyzer analyzer, IReadOnlyList<Diagnostic> diagnostics)
    {
        var builder = new StringBuilder();

        for (var i = 0; i < diagnostics.Count; ++i)
        {
            builder.AppendLine("// " + diagnostics[i]);

            var analyzerType = analyzer.GetType();
            var rules = analyzer.SupportedDiagnostics;

            foreach (var rule in rules)
            {
                if (rule.Id
                    != diagnostics[i]
                        .Id)
                {
                    continue;
                }

                var location = diagnostics[i]
                    .Location;

                if (location == Location.None)
                {
                    builder.Append($"GetGlobalResult({analyzerType.Name}.{rule.Id})");
                }
                else
                {
                    if (!location.IsInSource)
                    {
                        var msg =
                            $"Тестовая база в настоящее время не обрабатывает диагностику в расположениях метаданных. Диагностика в метаданных: {diagnostics[i]}{Environment.NewLine}";

                        throw new(msg);
                    }

                    var locationSourceTree = diagnostics[i]
                        .Location.SourceTree;

                    var resultMethodName = locationSourceTree != null && locationSourceTree.FilePath.EndsWith(".cs")
                        ? "GetCSharpResultAt"
                        : "GetBasicResultAt";

                    var linePosition = diagnostics[i]
                        .Location.GetLineSpan()
                        .StartLinePosition;

                    builder.Append(
                        $"{resultMethodName}({linePosition.Line + 1}, {linePosition.Character + 1}, {analyzerType.Name}.{rule.Id})");
                }

                if (i != diagnostics.Count - 1)
                {
                    builder.Append(',');
                }

                builder.AppendLine();

                break;
            }
        }

        return builder.ToString();
    }

    #endregion
}

public struct DiagnosticResultLocation
{
    public DiagnosticResultLocation(string path, int line, int column)
    {
        if (line < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(line), "line must be >= -1");
        }

        if (column < -1)
        {
            throw new ArgumentOutOfRangeException(nameof(column), "column must be >= -1");
        }

        Path = path;
        Line = line;
        Column = column;
    }

    public string Path { get; }

    public int Line { get; }

    public int Column { get; }
}

public struct DiagnosticResult
{
    private DiagnosticResultLocation[] _locations;

    public DiagnosticResultLocation[] Locations
    {
        get => _locations ??= Array.Empty<DiagnosticResultLocation>();
        set => _locations = value;
    }

    public DiagnosticSeverity Severity { get; set; }

    public string Id { get; set; }

    public string Message { get; set; }

    public string Path =>
        Locations.Length > 0
            ? Locations[0]
                .Path
            : "";

    public int Line =>
        Locations.Length > 0
            ? Locations[0]
                .Line
            : -1;

    public int Column =>
        Locations.Length > 0
            ? Locations[0]
                .Column
            : -1;
}

internal struct VerifyDiagnosticAnalyzerResult
{
    public bool Success { get; private set; }

    public string ErrorMessage { get; private set; }

    public static VerifyDiagnosticAnalyzerResult Ok() => new()
    {
        Success = true
    };

    public static VerifyDiagnosticAnalyzerResult Fail(string message) => new()
    {
        Success = false,
        ErrorMessage = message
    };
}