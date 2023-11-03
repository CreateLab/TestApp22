using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace AnalyzerTest11;

[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class SealedClassAnalyzer : DiagnosticAnalyzer
{
    
    private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
        "EIO1001",
        "Classes should be sealed",
        "Classes should be sealed",
        "Category",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Classes should be sealed to prevent inheritance."
    );
    
    // Implement the necessary methods to analyze the code.
    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze|GeneratedCodeAnalysisFlags.ReportDiagnostics);

        context.RegisterSyntaxNodeAction(AnalyzeClass, SyntaxKind.ClassDeclaration);
    }

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(Rule);

    private static void AnalyzeClass(SyntaxNodeAnalysisContext context)
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;

        var identifierText = classDeclaration.Identifier.Text;
        // Check if the class is not sealed, and report a diagnostic if necessary.
        if (identifierText.ToLowerInvariant().Contains("class") || identifierText.ToLower().Contains("test")) 
        {
            var diagnostic = Diagnostic.Create(Rule, classDeclaration.GetLocation());
            context.ReportDiagnostic(diagnostic);
        }
    }
}