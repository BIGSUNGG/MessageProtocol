using Microsoft.CodeAnalysis;
using System.Text;

namespace MessageProtocol.CodeGenerator
{
    /// <summary>
    /// Builds a unique identifier suffix from a symbol.
    /// Includes the namespace, nested-type chain, and generic arguments so identically named symbols never collide;
    /// if the name scheme collides anyway, a discriminator is appended from the set of used suffixes.
    /// </summary>
    internal static class SymbolNaming
    {
        public static string MakeUniqueSuffix(INamedTypeSymbol symbol, HashSet<string> usedSuffixes)
        {
            var sb = new StringBuilder();
            if (symbol.ContainingNamespace != null && !symbol.ContainingNamespace.IsGlobalNamespace)
            {
                sb.Append(symbol.ContainingNamespace.ToDisplayString()).Append('.');
            }

            AppendTypeName(sb, symbol);

            string suffix = SanitizeIdentifier(sb.ToString());
            string unique = suffix;
            for (int discriminator = 2; !usedSuffixes.Add(unique); discriminator++)
            {
                unique = $"{suffix}_{discriminator}";
            }

            return unique;
        }

        static void AppendTypeName(StringBuilder sb, INamedTypeSymbol symbol)
        {
            if (symbol.ContainingType != null)
            {
                AppendTypeName(sb, symbol.ContainingType);
                sb.Append('+');
            }

            // MetadataName's generic-arity notation (`) is not a valid identifier, so it is replaced later.
            sb.Append(symbol.MetadataName);

            foreach (var typeArgument in symbol.TypeArguments)
            {
                sb.Append('[');
                AppendTypeArgument(sb, typeArgument);
                sb.Append(']');
            }
        }

        static void AppendTypeArgument(StringBuilder sb, ITypeSymbol typeArgument)
        {
            switch (typeArgument)
            {
                case IArrayTypeSymbol arrayType:
                    AppendTypeArgument(sb, arrayType.ElementType);
                    sb.Append("Array");
                    if (arrayType.Rank > 1)
                    {
                        sb.Append(arrayType.Rank);
                    }
                    break;
                case ITypeParameterSymbol typeParameter:
                    sb.Append(typeParameter.Name);
                    break;
                case INamedTypeSymbol namedType:
                    AppendTypeName(sb, namedType);
                    break;
                default:
                    sb.Append(typeArgument.MetadataName);
                    break;
            }
        }

        static string SanitizeIdentifier(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            foreach (char c in raw)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }

            return sb.ToString();
        }
    }
}
