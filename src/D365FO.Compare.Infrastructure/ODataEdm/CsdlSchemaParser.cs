using System.Xml;
using System.Xml.Linq;
using D365FO.Compare.Domain.Metadata;
using Microsoft.OData.Edm;
using Microsoft.OData.Edm.Csdl;

namespace D365FO.Compare.Infrastructure.ODataEdm;

public sealed record ParsedSchema(
    IReadOnlyList<EntitySchema> Entities,
    IReadOnlyList<EnumSchema> Enums,
    IReadOnlyList<ComplexTypeSchema> ComplexTypes);

public sealed class CsdlSchemaParser
{
    private static readonly XNamespace Edm = "http://docs.oasis-open.org/odata/ns/edm";

    public ParsedSchema Parse(Stream stream, CancellationToken cancellationToken = default)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 512L * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        cancellationToken.ThrowIfCancellationRequested();
        using var modelReader = document.CreateReader();
        if (!CsdlReader.TryParse(modelReader, out var model, out var errors))
            throw new InvalidDataException("Invalid OData CSDL: " + string.Join("; ", errors.Select(error => error.ErrorCode)));
        if (model.EntityContainer is null)
            throw new InvalidDataException("OData EntityContainer is missing.");

        var schemas = document.Descendants(Edm + "Schema").ToArray();
        var aliases = schemas.Where(schema => schema.Attribute("Alias") is not null)
            .ToDictionary(schema => (string)schema.Attribute("Alias")!, schema => (string)schema.Attribute("Namespace")!, StringComparer.Ordinal);
        string Qualify(string name)
        {
            var dot = name.IndexOf('.');
            return dot > 0 && aliases.TryGetValue(name[..dot], out var ns) ? ns + name[dot..] : name;
        }
        var declarations = schemas.SelectMany(schema => schema.Elements()
                .Where(element => element.Name == Edm + "EntityType" || element.Name == Edm + "ComplexType")
                .Select(element => (Name: (string)schema.Attribute("Namespace")! + "." + (string)element.Attribute("Name")!, Element: element)))
            .ToDictionary(item => item.Name, item => item.Element, StringComparer.Ordinal);
        var externalAnnotations = schemas.SelectMany(schema => schema.Elements(Edm + "Annotations"))
            .GroupBy(element => Qualify((string?)element.Attribute("Target") ?? ""), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.SelectMany(element => element.Elements(Edm + "Annotation")).ToArray(), StringComparer.Ordinal);

        IReadOnlyList<AnnotationSchema> Annotations(XElement? element, string target) =>
            (element?.Elements(Edm + "Annotation") ?? [])
            .Concat(externalAnnotations.GetValueOrDefault(target) ?? [])
            .Select(annotation => new AnnotationSchema(Qualify((string?)annotation.Attribute("Term") ?? ""),
                (string?)annotation.Attribute("Qualifier"), CanonicalXml(annotation)))
            .OrderBy(annotation => annotation.Term, StringComparer.Ordinal).ThenBy(annotation => annotation.Qualifier, StringComparer.Ordinal).ToArray();

        FieldSchema MapField(IEdmStructuralProperty property)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var owner = (IEdmSchemaType)property.DeclaringType;
            var ownerName = owner.FullTypeName();
            var element = declarations.GetValueOrDefault(ownerName)?.Elements(Edm + "Property")
                .SingleOrDefault(candidate => (string?)candidate.Attribute("Name") == property.Name);
            var itemType = property.Type.IsCollection() ? property.Type.AsCollection().ElementType() : property.Type;
            ObservedValue Facet(string name, string? effective = null) => element?.Attribute(name) is { } attribute
                ? ObservedValue.Declared(attribute.Value) : ObservedValue.Missing(effective);
            return new FieldSchema(property.Name, itemType.FullName(), property.Type.IsCollection(),
                Facet("Nullable", property.Type.IsNullable ? "true" : "false"), Facet("MaxLength"), Facet("Precision"),
                Facet("Scale", itemType.IsDecimal() ? "0" : null),
                itemType.Definition is IEdmEnumType enumType ? enumType.FullTypeName() : null,
                Annotations(element, ownerName + "/" + property.Name));
        }

        var entities = new List<EntitySchema>();
        foreach (var set in model.EntityContainer.EntitySets())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = set.EntityType;
            var fullName = type.FullTypeName();
            var typeElement = declarations.GetValueOrDefault(fullName);
            var key = type.Key().Select((property, ordinal) => new KeyFieldSchema(property.Name, ordinal)).ToArray();
            var keyElement = typeElement?.Element(Edm + "Key");
            if (keyElement is not null)
                key = keyElement.Elements(Edm + "PropertyRef").Select((property, ordinal) =>
                    new KeyFieldSchema((string)property.Attribute("Name")!, ordinal, (string?)property.Attribute("Alias"))).ToArray();
            var navigation = type.NavigationProperties().Select(property =>
            {
                var itemType = property.Type.IsCollection() ? property.Type.AsCollection().ElementType() : property.Type;
                var owner = ((IEdmSchemaType)property.DeclaringType).FullTypeName();
                var element = declarations.GetValueOrDefault(owner)?.Elements(Edm + "NavigationProperty")
                    .SingleOrDefault(candidate => (string?)candidate.Attribute("Name") == property.Name);
                return new NavigationSchema(property.Name, itemType.FullName(), property.Type.IsCollection(),
                    element?.Attribute("Nullable") is { } nullable ? ObservedValue.Declared(nullable.Value)
                        : ObservedValue.Missing(property.Type.IsNullable ? "true" : "false"), property.Partner?.Name);
            }).OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
            var setElement = schemas.SelectMany(schema => schema.Elements(Edm + "EntityContainer")).SelectMany(container => container.Elements(Edm + "EntitySet"))
                .SingleOrDefault(element => (string?)element.Attribute("Name") == set.Name);
            entities.Add(new EntitySchema(set.Name, fullName,
                type.StructuralProperties().Select(MapField).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray(), key, navigation,
                Annotations(typeElement, fullName).Concat(Annotations(setElement, model.EntityContainer.FullName() + "/" + set.Name)).ToArray()));
        }
        var enums = model.SchemaElements.OfType<IEdmEnumType>().Select(type => new EnumSchema(type.FullTypeName(),
            "Edm." + type.UnderlyingType.PrimitiveKind, type.IsFlags,
            type.Members.Select(member => new EnumMemberSchema(member.Name, member.Value.Value)).OrderBy(member => member.Name, StringComparer.Ordinal).ToArray()))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        var complex = model.SchemaElements.OfType<IEdmComplexType>().Select(type => new ComplexTypeSchema(type.FullTypeName(),
            type.StructuralProperties().Select(MapField).OrderBy(field => field.Name, StringComparer.Ordinal).ToArray()))
            .OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
        return new ParsedSchema(entities.OrderBy(entity => entity.EntitySetName, StringComparer.Ordinal).ToArray(), enums, complex);
    }

    private static string CanonicalXml(XElement element)
    {
        XElement Normalize(XElement source) => new(source.Name,
            source.Attributes().Where(attribute => !attribute.IsNamespaceDeclaration).OrderBy(attribute => attribute.Name.ToString(), StringComparer.Ordinal)
                .Select(attribute => new XAttribute(attribute.Name, attribute.Value)),
            source.Nodes().Select(node => node is XElement child ? (object)Normalize(child)
                : node is XText text && !string.IsNullOrWhiteSpace(text.Value) ? new XText(text.Value) : null));
        return Normalize(element).ToString(SaveOptions.DisableFormatting);
    }
}