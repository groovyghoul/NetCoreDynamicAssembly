using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace NetCoreDynamicAssembly
{

    class Program
    {
        static void Main(string[] args)
        {
            var x = new CodeGenTests();
            x.ShouldDebugSources();
            Console.WriteLine("Hello World!");
        }
    }
    public class CodeGenTests
    {
        public void ShouldDebugSources()
        {
            var code =
@"namespace Debuggable
{
    public class HelloWorld
    {
        public string Greet(string name)
        {
            System.Diagnostics.Debugger.Break();
            var result = ""Hello, "" + name;
            return result;
        }
    }
}
";
            var codeGenerator = new CodeGenerator();
            var assembly = codeGenerator.CreateAssembly(code);

            dynamic instance = assembly.CreateInstance("Debuggable.HelloWorld");

            // Set breakpoint here
            string result = instance.Greet("Roslyn");
        }
    }

    public class CodeGenerator
    {
        private readonly IList<MetadataReference> references = new List<MetadataReference>();
        private readonly IList<Assembly> assemblies = new List<Assembly>();
        public string[] HintPaths { get; set; }

        public Assembly CreateAssembly(string code)
        {
            var encoding = Encoding.UTF8;

            var assemblyName = Path.GetRandomFileName();
            var symbolsName = Path.ChangeExtension(assemblyName, "pdb");
            var sourceCodePath = "generated.cs";

            var buffer = encoding.GetBytes(code);
            var sourceText = SourceText.From(buffer, buffer.Length, encoding, canBeEmbedded: true);

            var syntaxTree = CSharpSyntaxTree.ParseText(
                sourceText,
                new CSharpParseOptions(),
                path: sourceCodePath);

            var syntaxRootNode = syntaxTree.GetRoot() as CSharpSyntaxNode;
            var encoded = CSharpSyntaxTree.Create(syntaxRootNode, null, sourceCodePath, encoding);

            ReferenceAssemblyContainingType<object>();
            ReferenceAssembly(typeof(Enumerable).GetTypeInfo().Assembly);

            var optimizationLevel = OptimizationLevel.Debug;

            CSharpCompilation compilation = CSharpCompilation.Create(
                assemblyName,
                syntaxTrees: new[] { encoded },
                references: references,
                options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary)
                    .WithOptimizationLevel(optimizationLevel)
                    .WithPlatform(Platform.AnyCpu)
            );

            using (var assemblyStream = new MemoryStream())
            using (var symbolsStream = new MemoryStream())
            {
                var emitOptions = new EmitOptions(
                        debugInformationFormat: DebugInformationFormat.PortablePdb,
                        pdbFilePath: symbolsName);

                var embeddedTexts = new List<EmbeddedText>
                {
                    EmbeddedText.FromSource(sourceCodePath, sourceText),
                };

                EmitResult result = compilation.Emit(
                    peStream: assemblyStream,
                    pdbStream: symbolsStream,
                    embeddedTexts: embeddedTexts,
                    options: emitOptions);

                if (!result.Success)
                {
                    var errors = new List<string>();

                    IEnumerable<Diagnostic> failures = result.Diagnostics.Where(diagnostic =>
                        diagnostic.IsWarningAsError ||
                        diagnostic.Severity == DiagnosticSeverity.Error);

                    foreach (Diagnostic diagnostic in failures)
                        errors.Add($"{diagnostic.Id}: {diagnostic.GetMessage()}");

                    throw new Exception(String.Join("\n", errors));
                }

                Console.WriteLine(code);

                assemblyStream.Seek(0, SeekOrigin.Begin);
                symbolsStream?.Seek(0, SeekOrigin.Begin);

                var assembly = AssemblyLoadContext.Default.LoadFromStream(assemblyStream, symbolsStream);
                return assembly;
            }
        }

        public void ReferenceAssemblyContainingType<T>()
        {
            ReferenceAssembly(typeof(T).GetTypeInfo().Assembly);
        }

        public void ReferenceAssembly(Assembly assembly)
        {
            if (assembly == null)
                return;

            if (assemblies.Contains(assembly))
                return;

            assemblies.Add(assembly);

            try
            {
                var referencePath = CreateAssemblyReference(assembly);

                if (referencePath == null)
                {
                    Console.WriteLine($"Could not make an assembly reference to {assembly.FullName}");
                    return;
                }

                var alreadyReferenced = references.Any(x => x.Display == referencePath);
                if (alreadyReferenced)
                    return;

                var reference = MetadataReference.CreateFromFile(referencePath);

                references.Add(reference);

                foreach (var assemblyName in assembly.GetReferencedAssemblies())
                {
                    var referencedAssembly = Assembly.Load(assemblyName);
                    ReferenceAssembly(referencedAssembly);
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"Could not make an assembly reference to {assembly.FullName}\n\n{e}");
            }
        }

        private string CreateAssemblyReference(Assembly assembly)
        {
            if (assembly.IsDynamic)
                return null;

            return string.IsNullOrEmpty(assembly.Location)
                ? GetPath(assembly)
                : assembly.Location;
        }


        private string GetPath(Assembly assembly)
        {
            return HintPaths?
                .Select(FindFile(assembly))
                .FirstOrDefault<string>(file => !String.IsNullOrEmpty(file));
        }

        private static Func<string, string> FindFile(Assembly assembly)
        {
            return hintPath =>
            {
                var name = assembly.GetName().Name;
                Console.WriteLine($"Find {name}.dll in {hintPath}");
                var files = Directory.GetFiles(hintPath, name + ".dll", SearchOption.AllDirectories);
                var firstOrDefault = files.FirstOrDefault();
                if (firstOrDefault != null)
                {
                    Console.WriteLine($"Found {name}.dll in {firstOrDefault}");
                }

                return firstOrDefault;
            };
        }
    }
}
