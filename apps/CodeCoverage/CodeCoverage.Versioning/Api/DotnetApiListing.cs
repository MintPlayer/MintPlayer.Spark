using System.Collections.Immutable;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace CodeCoverage.Versioning.Api;

/// <summary>
/// The public API of a built assembly as sorted text lines (PRD D8, plan S3): every type and member a consumer can
/// reach (public, protected, protected internal), read from metadata. Nothing is loaded or executed. Attributes,
/// parameter names and private implementation details are left out, so a rebuild with another compiler or SDK of the
/// same source gives the same lines, and only a change a consumer can see changes them.
/// </summary>
public static class DotnetApiListing
{
	public static IReadOnlyList<string> List(Stream assembly)
	{
		using var pe = new PEReader(assembly, PEStreamOptions.LeaveOpen);
		if (!pe.HasMetadata) return [];
		var md = pe.GetMetadataReader();
		var provider = new SignatureNames(md);
		var lines = new List<string>();

		foreach (var handle in md.TypeDefinitions)
		{
			var type = md.GetTypeDefinition(handle);
			if (!IsVisible(md, type)) continue;
			var typeName = provider.TypeDefName(handle);
			// A sealed type's protected members cannot be reached; the compiler still emits protected overrides there.
			var isSealed = (type.Attributes & TypeAttributes.Sealed) != 0;
			var typeKind = TypeKind(md, type);
			var baseList = BaseList(md, type, provider);
			if (typeKind == "enum")
			{
				// The underlying type is part of the contract (casts, serialized size); it lives on the value__ field.
				foreach (var fh in type.GetFields())
				{
					var f = md.GetFieldDefinition(fh);
					if (md.GetString(f.Name) == "value__") baseList = " : " + f.DecodeSignature(provider, null);
				}
			}
			lines.Add($"{typeKind} {typeName}{TypeGenericParameters(md, type.GetGenericParameters(), provider)}{baseList}");

			foreach (var fh in type.GetFields())
			{
				var f = md.GetFieldDefinition(fh);
				if (!IsVisible(f.Attributes & FieldAttributes.FieldAccessMask, isSealed)) continue;
				if ((f.Attributes & FieldAttributes.SpecialName) != 0 && md.GetString(f.Name) == "value__") continue;
				var mods = Access(f.Attributes & FieldAttributes.FieldAccessMask)
					+ ((f.Attributes & FieldAttributes.Literal) != 0 ? " const" : (f.Attributes & FieldAttributes.Static) != 0 ? " static" : "")
					+ ((f.Attributes & FieldAttributes.InitOnly) != 0 ? " readonly" : "");
				var constant = (f.Attributes & FieldAttributes.Literal) != 0 && !f.GetDefaultValue().IsNil
					? " = " + ConstantText(md, md.GetConstant(f.GetDefaultValue()))
					: "";
				lines.Add($"{typeName} {mods} field {f.DecodeSignature(provider, null)} {md.GetString(f.Name)}{constant}");
			}

			var accessorOwners = new Dictionary<MethodDefinitionHandle, string>();
			foreach (var ph in type.GetProperties())
			{
				var p = md.GetPropertyDefinition(ph);
				var acc = p.GetAccessors();
				var sig = p.DecodeSignature(provider, null);
				var parts = new List<string>();
				foreach (var (kind, mh) in new[] { ("get", acc.Getter), ("set", acc.Setter) })
				{
					if (mh.IsNil) continue;
					var m = md.GetMethodDefinition(mh);
					accessorOwners[mh] = "property";
					if (!IsVisible(m.Attributes & MethodAttributes.MemberAccessMask, isSealed)) continue;
					var init = kind == "set" && IsInitOnly(md, m) ? "init" : kind;
					parts.Add($"{Access(m.Attributes & MethodAttributes.MemberAccessMask)} {MethodModifiers(m.Attributes)}{init}".Replace("  ", " "));
				}
				if (parts.Count == 0) continue;
				var indexer = sig.ParameterTypes.Length > 0 ? "[" + string.Join(", ", sig.ParameterTypes) + "]" : "";
				lines.Add($"{typeName} property {sig.ReturnType} {md.GetString(p.Name)}{indexer} {{ {string.Join("; ", parts)} }}");
			}

			foreach (var eh in type.GetEvents())
			{
				var e = md.GetEventDefinition(eh);
				var acc = e.GetAccessors();
				foreach (var mh in new[] { acc.Adder, acc.Remover, acc.Raiser }) if (!mh.IsNil) accessorOwners[mh] = "event";
				if (acc.Adder.IsNil) continue;
				var adder = md.GetMethodDefinition(acc.Adder);
				if (!IsVisible(adder.Attributes & MethodAttributes.MemberAccessMask, isSealed)) continue;
				lines.Add($"{typeName} {Access(adder.Attributes & MethodAttributes.MemberAccessMask)} {MethodModifiers(adder.Attributes)}event {provider.TypeName(e.Type)} {md.GetString(e.Name)}".Replace("  ", " "));
			}

			foreach (var mh in type.GetMethods())
			{
				if (accessorOwners.ContainsKey(mh)) continue;
				var m = md.GetMethodDefinition(mh);
				if (!IsVisible(m.Attributes & MethodAttributes.MemberAccessMask, isSealed)) continue;
				var name = md.GetString(m.Name);
				var sig = m.DecodeSignature(provider, null);
				var gps = TypeGenericParameters(md, m.GetGenericParameters(), provider);
				var parameters = ParameterList(md, m, sig);
				lines.Add($"{typeName} {Access(m.Attributes & MethodAttributes.MemberAccessMask)} {MethodModifiers(m.Attributes)}method {sig.ReturnType} {name}{gps}({parameters})".Replace("  ", " "));
			}
		}

		lines.Sort(StringComparer.Ordinal);
		return lines;
	}

	public static string Hash(IEnumerable<string> lines)
	{
		var text = string.Join("\n", lines) + "\n";
		return "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
	}

	static bool IsVisible(MetadataReader md, TypeDefinition type)
	{
		var vis = type.Attributes & TypeAttributes.VisibilityMask;
		switch (vis)
		{
			case TypeAttributes.Public: return true;
			case TypeAttributes.NestedPublic:
			case TypeAttributes.NestedFamily:
			case TypeAttributes.NestedFamORAssem:
				var outer = md.GetTypeDefinition(type.GetDeclaringType());
				if (vis != TypeAttributes.NestedPublic && (outer.Attributes & TypeAttributes.Sealed) != 0) return false;
				return IsVisible(md, outer);
			default: return false;
		}
	}

	static bool IsVisible(FieldAttributes access, bool sealedType) => access == FieldAttributes.Public || (!sealedType && access is FieldAttributes.Family or FieldAttributes.FamORAssem);
	static bool IsVisible(MethodAttributes access, bool sealedType) => access == MethodAttributes.Public || (!sealedType && access is MethodAttributes.Family or MethodAttributes.FamORAssem);

	static string Access(FieldAttributes access) => access == FieldAttributes.Public ? "public" : "protected";
	static string Access(MethodAttributes access) => access == MethodAttributes.Public ? "public" : "protected";

	static string MethodModifiers(MethodAttributes a)
	{
		var sb = new StringBuilder();
		if ((a & MethodAttributes.Static) != 0) sb.Append("static ");
		if ((a & MethodAttributes.Abstract) != 0) sb.Append("abstract ");
		else if ((a & MethodAttributes.Virtual) != 0 && (a & MethodAttributes.Final) == 0) sb.Append("virtual ");
		return sb.ToString();
	}

	static string TypeKind(MetadataReader md, TypeDefinition type)
	{
		if ((type.Attributes & TypeAttributes.Interface) != 0) return "interface";
		var baseName = BaseTypeFullName(md, type);
		if (baseName == "System.Enum") return "enum";
		if (baseName == "System.ValueType") return "struct";
		if (baseName == "System.MulticastDelegate") return "delegate";
		var sealedAbstract = (type.Attributes & (TypeAttributes.Sealed | TypeAttributes.Abstract));
		return sealedAbstract switch
		{
			TypeAttributes.Sealed | TypeAttributes.Abstract => "static class",
			TypeAttributes.Sealed => "sealed class",
			TypeAttributes.Abstract => "abstract class",
			_ => "class",
		};
	}

	static string? BaseTypeFullName(MetadataReader md, TypeDefinition type)
	{
		var b = type.BaseType;
		if (b.IsNil || b.Kind != HandleKind.TypeReference) return null;
		var r = md.GetTypeReference((TypeReferenceHandle)b);
		return md.GetString(r.Namespace) + "." + md.GetString(r.Name);
	}

	static string BaseList(MetadataReader md, TypeDefinition type, SignatureNames provider)
	{
		var items = new List<string>();
		if (!type.BaseType.IsNil)
		{
			var b = provider.TypeName(type.BaseType);
			if (b is not ("System.Object" or "System.ValueType" or "System.Enum" or "System.MulticastDelegate")) items.Add(b);
		}
		var ifaces = new List<string>();
		foreach (var ih in type.GetInterfaceImplementations())
		{
			var name = provider.TypeName(md.GetInterfaceImplementation(ih).Interface);
			ifaces.Add(name);
		}
		ifaces.Sort(StringComparer.Ordinal);
		items.AddRange(ifaces);
		return items.Count == 0 ? "" : " : " + string.Join(", ", items);
	}

	static string TypeGenericParameters(MetadataReader md, GenericParameterHandleCollection gps, SignatureNames provider)
	{
		if (gps.Count == 0) return "";
		var parts = new List<string>();
		foreach (var gh in gps)
		{
			var gp = md.GetGenericParameter(gh);
			var constraints = new List<string>();
			var a = gp.Attributes;
			if ((a & GenericParameterAttributes.Covariant) != 0) constraints.Add("out");
			if ((a & GenericParameterAttributes.Contravariant) != 0) constraints.Add("in");
			if ((a & GenericParameterAttributes.ReferenceTypeConstraint) != 0) constraints.Add("class");
			if ((a & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0) constraints.Add("struct");
			if ((a & GenericParameterAttributes.DefaultConstructorConstraint) != 0) constraints.Add("new()");
			var types = new List<string>();
			foreach (var ch in gp.GetConstraints()) types.Add(provider.TypeName(md.GetGenericParameterConstraint(ch).Type));
			types.Sort(StringComparer.Ordinal);
			constraints.AddRange(types);
			parts.Add(constraints.Count == 0 ? $"`{gp.Index}" : $"`{gp.Index}:{string.Join("+", constraints)}");
		}
		return "<" + string.Join(", ", parts) + ">";
	}

	static string ParameterList(MetadataReader md, MethodDefinition m, MethodSignature<string> sig)
	{
		var mods = new string[sig.ParameterTypes.Length];
		var defaults = new string[sig.ParameterTypes.Length];
		foreach (var ph in m.GetParameters())
		{
			var p = md.GetParameter(ph);
			if (p.SequenceNumber == 0 || p.SequenceNumber > mods.Length) continue;
			var i = p.SequenceNumber - 1;
			if ((p.Attributes & ParameterAttributes.Out) != 0 && sig.ParameterTypes[i].EndsWith('&')) mods[i] = "out ";
			if ((p.Attributes & ParameterAttributes.HasDefault) != 0 && !p.GetDefaultValue().IsNil)
				defaults[i] = " = " + ConstantText(md, md.GetConstant(p.GetDefaultValue()));
			else if ((p.Attributes & ParameterAttributes.Optional) != 0)
				defaults[i] = " = default";
		}
		var parts = new string[sig.ParameterTypes.Length];
		for (var i = 0; i < parts.Length; i++) parts[i] = mods[i] + sig.ParameterTypes[i] + defaults[i];
		return string.Join(", ", parts);
	}

	static bool IsInitOnly(MetadataReader md, MethodDefinition setter)
	{
		var sig = md.GetBlobReader(setter.Signature);
		var header = sig.ReadSignatureHeader();
		if (header.IsGeneric) sig.ReadCompressedInteger();
		sig.ReadCompressedInteger();
		var code = sig.ReadSignatureTypeCode();
		while (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
		{
			var h = sig.ReadTypeHandle();
			if (h.Kind == HandleKind.TypeReference)
			{
				var r = md.GetTypeReference((TypeReferenceHandle)h);
				if (md.GetString(r.Name) == "IsExternalInit") return true;
			}
			code = sig.ReadSignatureTypeCode();
		}
		return false;
	}

	static string ConstantText(MetadataReader md, Constant c)
	{
		var blob = md.GetBlobReader(c.Value);
		return c.TypeCode switch
		{
			ConstantTypeCode.NullReference => "null",
			ConstantTypeCode.String => "\"" + blob.ReadUTF16(blob.Length) + "\"",
			ConstantTypeCode.Boolean => blob.ReadBoolean() ? "true" : "false",
			ConstantTypeCode.Char => "'" + blob.ReadChar() + "'",
			ConstantTypeCode.SByte => blob.ReadSByte().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.Byte => blob.ReadByte().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.Int16 => blob.ReadInt16().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.UInt16 => blob.ReadUInt16().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.Int32 => blob.ReadInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.UInt32 => blob.ReadUInt32().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.Int64 => blob.ReadInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.UInt64 => blob.ReadUInt64().ToString(System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.Single => blob.ReadSingle().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
			ConstantTypeCode.Double => blob.ReadDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
			_ => "?",
		};
	}

	/// <summary>Turns signature blobs into stable type names: full namespace, generic arity, no assembly names.</summary>
	sealed class SignatureNames(MetadataReader md) : ISignatureTypeProvider<string, object?>
	{
		public string TypeName(EntityHandle handle) => handle.Kind switch
		{
			HandleKind.TypeDefinition => TypeDefName((TypeDefinitionHandle)handle),
			HandleKind.TypeReference => GetTypeFromReference(md, (TypeReferenceHandle)handle, 0),
			HandleKind.TypeSpecification => md.GetTypeSpecification((TypeSpecificationHandle)handle).DecodeSignature(this, null),
			_ => "?",
		};

		public string TypeDefName(TypeDefinitionHandle handle)
		{
			var t = md.GetTypeDefinition(handle);
			var name = md.GetString(t.Name);
			if (t.IsNested) return TypeDefName(t.GetDeclaringType()) + "+" + name;
			var ns = md.GetString(t.Namespace);
			return ns.Length == 0 ? name : ns + "." + name;
		}

		public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";
		public string GetByReferenceType(string elementType) => elementType + "&";
		public string GetFunctionPointerType(MethodSignature<string> signature) => "delegate*<" + string.Join(", ", signature.ParameterTypes.Append(signature.ReturnType)) + ">";
		public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(", ", typeArguments) + ">";
		public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
		public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
		// Modifiers carry `in`/`readonly`/`init`; unmanaged and volatile are kept, nullability lives in attributes and is left out.
		public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => isRequired ? unmodifiedType + " modreq(" + modifier + ")" : unmodifiedType;
		public string GetPinnedType(string elementType) => elementType;
		public string GetPointerType(string elementType) => elementType + "*";
		public string GetPrimitiveType(PrimitiveTypeCode typeCode) => "System." + typeCode switch
		{
			PrimitiveTypeCode.IntPtr => "IntPtr",
			PrimitiveTypeCode.UIntPtr => "UIntPtr",
			PrimitiveTypeCode.TypedReference => "TypedReference",
			_ => typeCode.ToString(),
		};
		public string GetSZArrayType(string elementType) => elementType + "[]";
		public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) => TypeDefName(handle);
		public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
		{
			var r = reader.GetTypeReference(handle);
			var name = reader.GetString(r.Name);
			if (r.ResolutionScope.Kind == HandleKind.TypeReference) return GetTypeFromReference(reader, (TypeReferenceHandle)r.ResolutionScope, 0) + "+" + name;
			var ns = reader.GetString(r.Namespace);
			return ns.Length == 0 ? name : ns + "." + name;
		}
		public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
			reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
	}
}
