using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace PayPlanner.TestShards;

public static class AssemblyCatalog
{
    public static HashSet<string> ReadTheoryMethods(string assembly)
    {
        using var stream = File.OpenRead(assembly);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var theories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var handle in metadata.CustomAttributes)
        {
            var attribute = metadata.GetCustomAttribute(handle);
            var name = AttributeName(metadata, attribute.Constructor);
            ShardPlan.Require(!IsUnsupportedCollectionAttribute(name), "Explicit xUnit collection metadata requires an atomic collection-aware plan.");
            if (name == "Xunit.TheoryAttribute" && attribute.Parent.Kind == HandleKind.MethodDefinition)
            {
                var method = metadata.GetMethodDefinition((MethodDefinitionHandle)attribute.Parent);
                theories.Add(TypeName(metadata, method.GetDeclaringType()) + "." + metadata.GetString(method.Name));
            }
        }
        return theories;
    }

    public static bool IsUnsupportedCollectionAttribute(string name)
        => name is "Xunit.CollectionAttribute" or "Xunit.CollectionDefinitionAttribute" or "Xunit.CollectionBehaviorAttribute";

    public static void RequireEnumeratedTheories(IEnumerable<DiscoveredCase> cases, IReadOnlySet<string> theoryMethods)
    {
        foreach (var row in cases)
            ShardPlan.Require(!theoryMethods.Contains(row.FullyQualifiedName) || row.DisplayName != row.FullyQualifiedName,
                "A deferred theory has no independently discovered row identities. Use serializable test parameters.");
    }

    private static string AttributeName(MetadataReader metadata, EntityHandle constructor)
        => constructor.Kind switch
        {
            HandleKind.MemberReference => TypeName(metadata, metadata.GetMemberReference((MemberReferenceHandle)constructor).Parent),
            HandleKind.MethodDefinition => TypeName(metadata, metadata.GetMethodDefinition((MethodDefinitionHandle)constructor).GetDeclaringType()),
            _ => throw new InvalidDataException("Unsupported attribute constructor metadata."),
        };

    private static string TypeName(MetadataReader metadata, EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeReference)
        {
            var type = metadata.GetTypeReference((TypeReferenceHandle)handle);
            var name = metadata.GetString(type.Name);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference) return TypeName(metadata, type.ResolutionScope) + "+" + name;
            var space = metadata.GetString(type.Namespace);
            return string.IsNullOrEmpty(space) ? name : space + "." + name;
        }
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var type = metadata.GetTypeDefinition((TypeDefinitionHandle)handle);
            var name = metadata.GetString(type.Name);
            var parent = type.GetDeclaringType();
            if (!parent.IsNil) return TypeName(metadata, parent) + "+" + name;
            var space = metadata.GetString(type.Namespace);
            return string.IsNullOrEmpty(space) ? name : space + "." + name;
        }
        throw new InvalidDataException("Unsupported attribute type metadata.");
    }
}
