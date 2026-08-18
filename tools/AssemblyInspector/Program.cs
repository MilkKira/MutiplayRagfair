using System.Reflection;
using System.Runtime.Loader;
using Mono.Cecil;

if (args.Length < 1)
{
    Console.Error.WriteLine("Usage: AssemblyInspector <assembly> [type-filter ...]");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var referenceDirectory = Path.GetDirectoryName(assemblyPath)!;

if (args is [_, "--il", var ilTypeName, var ilMethodName, ..])
{
    using var module = ModuleDefinition.ReadModule(assemblyPath);
    var ilType = module.Types.SelectMany(Flatten).FirstOrDefault(x => x.FullName == ilTypeName)
        ?? throw new InvalidOperationException($"IL type not found: {ilTypeName}");
    var candidates = ilType.Methods.Where(x => x.Name == ilMethodName).ToArray();
    foreach (var candidate in candidates)
    {
        Console.WriteLine($"IL Method: {candidate.FullName}");
        if (!candidate.HasBody) { Console.WriteLine("<no body>"); continue; }
        foreach (var variable in candidate.Body.Variables)
            Console.WriteLine($"Local V_{variable.Index}: {variable.VariableType.FullName}");
        foreach (var instruction in candidate.Body.Instructions)
            Console.WriteLine($"{instruction.Offset:X4}: {instruction.OpCode} {instruction.Operand}");
    }
    return candidates.Length == 0 ? 3 : 0;
}

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(referenceDirectory, $"{name.Name}.dll");
    if (File.Exists(candidate)) return AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate);
    var packageName = name.Name!.ToLowerInvariant();
    var packageRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages",
        packageName, name.Version is null ? string.Empty : $"{name.Version.Major}.{name.Version.Minor}.{name.Version.Build}");
    var packageCandidate = Directory.Exists(packageRoot)
        ? Directory.GetFiles(packageRoot, $"{name.Name}.dll", SearchOption.AllDirectories).FirstOrDefault()
        : null;
    return packageCandidate is not null ? AssemblyLoadContext.Default.LoadFromAssemblyPath(packageCandidate) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
Console.WriteLine($"Assembly: {assembly.FullName}");
foreach (var reference in assembly.GetReferencedAssemblies().OrderBy(x => x.Name))
{
    Console.WriteLine($"Reference: {reference.FullName}");
}
try
{
    foreach (var attribute in assembly.GetCustomAttributesData())
    {
        Console.WriteLine(
            $"Attribute: {attribute.AttributeType.FullName}({string.Join(", ", attribute.ConstructorArguments.Select(x => x.Value))})"
        );
    }
}
catch (Exception exception)
{
    Console.WriteLine($"Attributes unavailable: {exception.GetType().Name}: {exception.Message}");
}

var filters = args.Skip(1).ToArray();
if (filters is ["--contains", var fragment])
{
    try
    {
        foreach (var match in assembly.GetTypes().Where(x => x.FullName?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true))
        {
            Console.WriteLine($"Matching type: {match.FullName}");
        }
    }
    catch (ReflectionTypeLoadException exception)
    {
        foreach (var match in exception.Types.Where(x => x?.FullName?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true))
        {
            Console.WriteLine($"Matching type: {match!.FullName}");
        }

        foreach (var loaderException in exception.LoaderExceptions.Where(x => x is not null).DistinctBy(x => x!.Message))
        {
            Console.WriteLine($"Loader warning: {loaderException!.Message}");
        }
    }

    return 0;
}

foreach (var filter in filters)
{
    var type = assembly.GetType(filter, throwOnError: false, ignoreCase: false);
    if (type is null)
    {
        Console.WriteLine($"TYPE NOT FOUND: {filter}");
        continue;
    }

    Console.WriteLine();
    Console.WriteLine($"Type: {type.FullName}");
    Console.WriteLine($"Kind: {(type.IsInterface ? "interface" : type.IsAbstract ? "abstract" : "concrete")}");
    Console.WriteLine($"Base: {type.BaseType?.FullName ?? "<none>"}");
    Console.WriteLine($"Interfaces: {string.Join(", ", type.GetInterfaces().Select(x => x.FullName))}");

    const BindingFlags flags =
        BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.Static
        | BindingFlags.DeclaredOnly;

    foreach (var field in type.GetFields(flags).OrderBy(x => x.Name))
    {
        try
        {
            var value = field.IsLiteral ? $" = {field.GetRawConstantValue()}" : string.Empty;
            Console.WriteLine($"Field: {FormatType(field.FieldType)} {field.Name}{value}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Field: {field.Name} <type unavailable: {exception.GetType().Name}>");
        }
    }

    foreach (var property in type.GetProperties(flags).OrderBy(x => x.Name))
    {
        try
        {
            var accessor = property.GetMethod ?? property.SetMethod;
            Console.WriteLine($"Property: {FormatType(property.PropertyType)} {property.Name} "
                + $"abstract={accessor?.IsAbstract} virtual={accessor?.IsVirtual}");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Property: {property.Name} <type unavailable: {exception.GetType().Name}>");
        }
    }

    foreach (var constructor in type.GetConstructors(flags).OrderBy(x => x.GetParameters().Length))
    {
        try
        {
            var parameters = string.Join(", ", constructor.GetParameters().Select(x => $"{FormatType(x.ParameterType)} {x.Name}"));
            Console.WriteLine($"Constructor: {type.FullName}({parameters})");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Constructor: <signature unavailable: {exception.GetType().Name}>");
        }
    }

    foreach (var method in type.GetMethods(flags).OrderBy(x => x.Name))
    {
        try
        {
            var parameters = string.Join(", ", method.GetParameters().Select(x => $"{FormatType(x.ParameterType)} {x.Name}"));
            Console.WriteLine(
                $"Method: {(method.IsPublic ? "public" : method.IsFamily ? "protected" : "nonpublic")} "
                    + $"{(method.IsStatic ? "static " : string.Empty)}{FormatType(method.ReturnType)} {method.Name}({parameters})"
            );
        }
        catch (Exception exception)
        {
            Console.WriteLine($"Method: {method.Name} <signature unavailable: {exception.GetType().Name}>");
        }
    }
}

return 0;

static string FormatType(Type type)
{
    if (type.IsGenericType)
    {
        var name = type.GetGenericTypeDefinition().FullName!;
        name = name[..name.IndexOf('`')];
        return $"{name}<{string.Join(", ", type.GetGenericArguments().Select(FormatType))}>";
    }

    return type.FullName ?? type.Name;
}

static IEnumerable<TypeDefinition> Flatten(TypeDefinition type)
{
    yield return type;
    foreach (var nested in type.NestedTypes.SelectMany(Flatten)) yield return nested;
}
