namespace CodeCoverage.Entities;

/// <summary>
/// The target of <see cref="ApiToken.AccountOwnerKey"/>: a forge owner, identified by its
/// <c>provider:login</c> key. Not a document and never stored.
/// </summary>
/// <remarks>
/// <c>[Reference]</c> needs a CLR type, and the rows the picker lists — <c>Custom.MyAccounts</c>,
/// the same query as Home's "Your accounts" — are the clrType-less <c>MyAccountRow</c>, whose row id
/// is exactly that key. This type only names the target so the synchronizer keeps the attribute a
/// Reference; the value is stored verbatim, as <see cref="EventColumnMapping.TargetColumnOptionId"/>
/// stores a <see cref="ProjectColumn"/> option id.
/// </remarks>
public sealed class AccountOwner
{
    private AccountOwner() { }
}
