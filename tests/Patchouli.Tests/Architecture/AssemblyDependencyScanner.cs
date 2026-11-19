using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Patchouli.Tests.Architecture;

/// <summary>One compiled dependency from a top-level type in the scanned assembly to a type in a target assembly.</summary>
internal sealed record TypeDependency(string Owner, string TargetAssembly, string TargetType)
{
    public static bool TryParse(string line, out TypeDependency dependency)
    {
        dependency = null!;
        int arrow = line.IndexOf(" -> ", StringComparison.Ordinal);
        int bang = arrow < 0 ? -1 : line.IndexOf('!', arrow);
        if (arrow <= 0 || bang < 0)
        {
            return false;
        }

        dependency = new TypeDependency(line[..arrow], line[(arrow + 4)..bang], line[(bang + 1)..]);
        return true;
    }

    public override string ToString()
    {
        return $"{Owner} -> {TargetAssembly}!{TargetType}";
    }
}

/// <summary>
/// Reads compiled IL metadata rather than source text, so fully qualified names, using aliases,
/// <c>var</c> locals, generic arguments, lambdas/async state machines and member return types are
/// all observed as the compiler emitted them. Dependencies are attributed to the outermost
/// declaring type so compiler-generated nested types count against their source owner.
/// </summary>
internal static class AssemblyDependencyScanner
{
    private static readonly OpCode[] OneByteOpCodes = new OpCode[0x100];
    private static readonly OpCode[] TwoByteOpCodes = new OpCode[0x100];

    static AssemblyDependencyScanner()
    {
        foreach (FieldInfo field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode code)
            {
                continue;
            }

            ushort value = unchecked((ushort)code.Value);
            if (code.Size == 1)
            {
                OneByteOpCodes[value] = code;
            }
            else
            {
                TwoByteOpCodes[value & 0xff] = code;
            }
        }
    }

    public static IReadOnlySet<TypeDependency> Scan(string assemblyPath, IReadOnlySet<string> targetAssemblies)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader peReader = new(stream);
        return Scan(peReader, targetAssemblies);
    }

    public static IReadOnlySet<TypeDependency> Scan(byte[] image, IReadOnlySet<string> targetAssemblies)
    {
        using PEReader peReader = new(ImmutableArray.Create(image));
        return Scan(peReader, targetAssemblies);
    }

    private static IReadOnlySet<TypeDependency> Scan(PEReader peReader, IReadOnlySet<string> targetAssemblies)
    {
        MetadataReader reader = peReader.GetMetadataReader();
        Collector collector = new(peReader, reader, targetAssemblies);
        foreach (TypeDefinitionHandle handle in reader.TypeDefinitions)
        {
            collector.ScanType(handle);
        }

        return collector.Result;
    }

    private sealed class Collector(PEReader peReader, MetadataReader reader, IReadOnlySet<string> targets)
    {
        private readonly HashSet<TypeDependency> _result = [];
        private readonly SignatureProvider _provider = new(reader);
        private string _owner = string.Empty;

        public IReadOnlySet<TypeDependency> Result => _result;

        public void ScanType(TypeDefinitionHandle handle)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            _owner = OwnerName(handle);
            if (_owner == "<Module>")
            {
                return;
            }

            AddEntity(type.BaseType);
            foreach (InterfaceImplementationHandle implementation in type.GetInterfaceImplementations())
            {
                AddEntity(reader.GetInterfaceImplementation(implementation).Interface);
            }

            AddGenericConstraints(type.GetGenericParameters());
            AddAttributes(type.GetCustomAttributes());
            foreach (FieldDefinitionHandle fieldHandle in type.GetFields())
            {
                FieldDefinition field = reader.GetFieldDefinition(fieldHandle);
                AddAll(field.DecodeSignature(_provider, null));
                AddAttributes(field.GetCustomAttributes());
            }

            foreach (MethodDefinitionHandle methodHandle in type.GetMethods())
            {
                ScanMethod(methodHandle);
            }

            foreach (PropertyDefinitionHandle propertyHandle in type.GetProperties())
            {
                PropertyDefinition property = reader.GetPropertyDefinition(propertyHandle);
                MethodSignature<ImmutableArray<TypeDependencyTarget>> signature =
                    property.DecodeSignature(_provider, null);
                AddSignature(signature);
                AddAttributes(property.GetCustomAttributes());
            }

            foreach (EventDefinitionHandle eventHandle in type.GetEvents())
            {
                EventDefinition definition = reader.GetEventDefinition(eventHandle);
                AddEntity(definition.Type);
                AddAttributes(definition.GetCustomAttributes());
            }
        }

        private void ScanMethod(MethodDefinitionHandle handle)
        {
            MethodDefinition method = reader.GetMethodDefinition(handle);
            AddSignature(method.DecodeSignature(_provider, null));
            AddGenericConstraints(method.GetGenericParameters());
            AddAttributes(method.GetCustomAttributes());
            foreach (ParameterHandle parameter in method.GetParameters())
            {
                AddAttributes(reader.GetParameter(parameter).GetCustomAttributes());
            }

            if (method.RelativeVirtualAddress == 0)
            {
                return;
            }

            MethodBodyBlock body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            if (!body.LocalSignature.IsNil)
            {
                StandaloneSignature locals = reader.GetStandaloneSignature(body.LocalSignature);
                foreach (ImmutableArray<TypeDependencyTarget> local in locals.DecodeLocalSignature(_provider, null))
                {
                    AddAll(local);
                }
            }

            foreach (ExceptionRegion region in body.ExceptionRegions)
            {
                AddEntity(region.CatchType);
            }

            ScanIl(body.GetILReader());
        }

        private void ScanIl(BlobReader il)
        {
            while (il.RemainingBytes > 0)
            {
                byte first = il.ReadByte();
                OpCode code = first == 0xFE ? TwoByteOpCodes[il.ReadByte()] : OneByteOpCodes[first];
                switch (code.OperandType)
                {
                    case OperandType.InlineNone:
                        break;
                    case OperandType.ShortInlineBrTarget:
                    case OperandType.ShortInlineI:
                    case OperandType.ShortInlineVar:
                        il.Offset += 1;
                        break;
                    case OperandType.InlineVar:
                        il.Offset += 2;
                        break;
                    case OperandType.InlineI:
                    case OperandType.InlineBrTarget:
                    case OperandType.InlineString:
                    case OperandType.ShortInlineR:
                        il.Offset += 4;
                        break;
                    case OperandType.InlineI8:
                    case OperandType.InlineR:
                        il.Offset += 8;
                        break;
                    case OperandType.InlineSwitch:
                        // Read the count before computing the jump-table end; a compound assignment would
                        // capture the offset before the count is consumed.
                        int branchCount = il.ReadInt32();
                        il.Offset += 4 * branchCount;
                        break;
                    case OperandType.InlineField:
                    case OperandType.InlineMethod:
                    case OperandType.InlineTok:
                    case OperandType.InlineType:
                    case OperandType.InlineSig:
                        AddToken(il.ReadInt32());
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported IL operand type {code.OperandType}.");
                }
            }
        }

        private void AddToken(int token)
        {
            Handle handle = MetadataTokens.Handle(token);
            if (handle.Kind == HandleKind.StandaloneSignature)
            {
                StandaloneSignature signature = reader.GetStandaloneSignature((StandaloneSignatureHandle)handle);
                if (signature.GetKind() == StandaloneSignatureKind.Method)
                {
                    AddSignature(signature.DecodeMethodSignature(_provider, null));
                }

                return;
            }

            AddEntity((EntityHandle)handle);
        }

        private void AddEntity(EntityHandle handle)
        {
            if (handle.IsNil)
            {
                return;
            }

            switch (handle.Kind)
            {
                case HandleKind.TypeReference:
                case HandleKind.TypeSpecification:
                    AddAll(_provider.FromHandle(handle));
                    break;
                case HandleKind.MemberReference:
                    MemberReference member = reader.GetMemberReference((MemberReferenceHandle)handle);
                    AddEntity(member.Parent);
                    if (member.GetKind() == MemberReferenceKind.Method)
                    {
                        AddSignature(member.DecodeMethodSignature(_provider, null));
                    }
                    else
                    {
                        AddAll(member.DecodeFieldSignature(_provider, null));
                    }

                    break;
                case HandleKind.MethodSpecification:
                    MethodSpecification specification =
                        reader.GetMethodSpecification((MethodSpecificationHandle)handle);
                    AddEntity(specification.Method);
                    foreach (ImmutableArray<TypeDependencyTarget> argument in
                             specification.DecodeSignature(_provider, null))
                    {
                        AddAll(argument);
                    }

                    break;
            }
        }

        private void AddSignature(MethodSignature<ImmutableArray<TypeDependencyTarget>> signature)
        {
            AddAll(signature.ReturnType);
            foreach (ImmutableArray<TypeDependencyTarget> parameter in signature.ParameterTypes)
            {
                AddAll(parameter);
            }
        }

        private void AddGenericConstraints(GenericParameterHandleCollection parameters)
        {
            foreach (GenericParameterHandle parameterHandle in parameters)
            {
                foreach (GenericParameterConstraintHandle constraint in
                         reader.GetGenericParameter(parameterHandle).GetConstraints())
                {
                    AddEntity(reader.GetGenericParameterConstraint(constraint).Type);
                }
            }
        }

        private void AddAttributes(CustomAttributeHandleCollection attributes)
        {
            foreach (CustomAttributeHandle attribute in attributes)
            {
                AddEntity(reader.GetCustomAttribute(attribute).Constructor);
            }
        }

        private void AddAll(ImmutableArray<TypeDependencyTarget> targetsToAdd)
        {
            foreach (TypeDependencyTarget target in targetsToAdd)
            {
                if (targets.Contains(target.Assembly))
                {
                    _result.Add(new TypeDependency(_owner, target.Assembly, target.Type));
                }
            }
        }

        private string OwnerName(TypeDefinitionHandle handle)
        {
            TypeDefinition type = reader.GetTypeDefinition(handle);
            while (type.GetDeclaringType() is { IsNil: false } declaring)
            {
                type = reader.GetTypeDefinition(declaring);
            }

            string name = reader.GetString(type.Name);
            string ns = reader.GetString(type.Namespace);
            return ns.Length == 0 ? name : $"{ns}.{name}";
        }
    }

    internal readonly record struct TypeDependencyTarget(string Assembly, string Type);

    private sealed class SignatureProvider(MetadataReader reader)
        : ISignatureTypeProvider<ImmutableArray<TypeDependencyTarget>, object?>
    {
        private static readonly ImmutableArray<TypeDependencyTarget> None = [];

        public ImmutableArray<TypeDependencyTarget> FromHandle(EntityHandle handle)
        {
            return handle.Kind switch
            {
                HandleKind.TypeReference => GetTypeFromReference(reader, (TypeReferenceHandle)handle, 0),
                HandleKind.TypeSpecification => GetTypeFromSpecification(reader, null,
                    (TypeSpecificationHandle)handle, 0),
                _ => None
            };
        }

        public ImmutableArray<TypeDependencyTarget> GetTypeFromReference(MetadataReader metadata,
            TypeReferenceHandle handle, byte rawTypeKind)
        {
            TypeReference type = metadata.GetTypeReference(handle);
            string name = metadata.GetString(type.Name);
            EntityHandle scope = type.ResolutionScope;
            string ns = metadata.GetString(type.Namespace);
            while (scope.Kind == HandleKind.TypeReference)
            {
                TypeReference outer = metadata.GetTypeReference((TypeReferenceHandle)scope);
                name = $"{metadata.GetString(outer.Name)}+{name}";
                ns = metadata.GetString(outer.Namespace);
                scope = outer.ResolutionScope;
            }

            if (scope.Kind != HandleKind.AssemblyReference)
            {
                return None;
            }

            string assembly = metadata.GetString(metadata.GetAssemblyReference((AssemblyReferenceHandle)scope).Name);
            return [new TypeDependencyTarget(assembly, ns.Length == 0 ? name : $"{ns}.{name}")];
        }

        public ImmutableArray<TypeDependencyTarget> GetTypeFromSpecification(MetadataReader metadata,
            object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        {
            return metadata.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
        }

        public ImmutableArray<TypeDependencyTarget> GetTypeFromDefinition(MetadataReader metadata,
            TypeDefinitionHandle handle, byte rawTypeKind)
        {
            return None;
        }

        public ImmutableArray<TypeDependencyTarget> GetGenericInstantiation(
            ImmutableArray<TypeDependencyTarget> genericType,
            ImmutableArray<ImmutableArray<TypeDependencyTarget>> typeArguments)
        {
            return typeArguments.Aggregate(genericType, (current, argument) => current.AddRange(argument));
        }

        public ImmutableArray<TypeDependencyTarget> GetModifiedType(ImmutableArray<TypeDependencyTarget> modifier,
            ImmutableArray<TypeDependencyTarget> unmodifiedType, bool isRequired)
        {
            return unmodifiedType.AddRange(modifier);
        }

        public ImmutableArray<TypeDependencyTarget> GetFunctionPointerType(
            MethodSignature<ImmutableArray<TypeDependencyTarget>> signature)
        {
            return signature.ParameterTypes.Aggregate(signature.ReturnType,
                (current, parameter) => current.AddRange(parameter));
        }

        public ImmutableArray<TypeDependencyTarget> GetArrayType(ImmutableArray<TypeDependencyTarget> elementType,
            ArrayShape shape)
        {
            return elementType;
        }

        public ImmutableArray<TypeDependencyTarget> GetByReferenceType(ImmutableArray<TypeDependencyTarget> elementType)
        {
            return elementType;
        }

        public ImmutableArray<TypeDependencyTarget> GetPinnedType(ImmutableArray<TypeDependencyTarget> elementType)
        {
            return elementType;
        }

        public ImmutableArray<TypeDependencyTarget> GetPointerType(ImmutableArray<TypeDependencyTarget> elementType)
        {
            return elementType;
        }

        public ImmutableArray<TypeDependencyTarget> GetSZArrayType(ImmutableArray<TypeDependencyTarget> elementType)
        {
            return elementType;
        }

        public ImmutableArray<TypeDependencyTarget> GetGenericMethodParameter(object? genericContext, int index)
        {
            return None;
        }

        public ImmutableArray<TypeDependencyTarget> GetGenericTypeParameter(object? genericContext, int index)
        {
            return None;
        }

        public ImmutableArray<TypeDependencyTarget> GetPrimitiveType(PrimitiveTypeCode typeCode)
        {
            return None;
        }

        public ImmutableArray<TypeDependencyTarget> GetSentinelType()
        {
            return None;
        }
    }
}
