using FsCheck;
using FsCheck.Fluent;
using LakeWright.Core.Tenancy;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Properties of the Unity Catalog identifier guard, checked against generated input.
/// </summary>
/// <remarks>
/// Catalog and schema names are the one place in the query path where a value reaches Databricks
/// as an identifier rather than as a bound parameter, because the Statement Execution API takes
/// them as separate fields and there is no parameter form for an identifier. Everything else is
/// parameterised; this is the seam.
///
/// <b>The generator is deliberately not <c>string</c>.</b> The first version of these tests asked
/// FsCheck for arbitrary strings, and they passed against a guard broken on purpose — anchored with
/// <c>$</c> instead of <c>\z</c>, which accepts a trailing newline. Random strings essentially never
/// land on "a valid identifier plus one hostile character", so the properties were describing a
/// check they were not performing. They generate near-misses now: a well-formed identifier with one
/// character spliced in, which is the shape an attack actually has.
/// </remarks>
[Trait("Category", "TenantIsolation")]
public class IdentifierPropertyTests
{
    /// <summary>
    /// Characters that end an identifier early, comment it out, or close a quote — plus the
    /// trailing newline that a <c>$</c>-anchored pattern silently accepts.
    /// </summary>
    private static readonly char[] Hostile =
    [
        '`', '\'', '"', ';', ' ', '\t', '\n', '\r', '.', '-', '/', '\\', '*', '(', ')',
        '\0', ' ', '‮', 'A', 'Z', 'İ',
    ];

    /// <summary>A well-formed identifier: what the guard is supposed to accept.</summary>
    private static Gen<string> ValidIdentifier() =>
        from first in Gen.Elements("abcdefghijklmnopqrstuvwxyz".ToCharArray())
        from rest in Gen.ListOf(Gen.Elements("abcdefghijklmnopqrstuvwxyz0123456789_".ToCharArray()))
        select first + new string([.. rest]);

    [Fact]
    public void A_well_formed_identifier_within_the_ceiling_is_accepted() =>
        Prop.ForAll(
            ValidIdentifier().Where(v => v.Length <= 63).ToArbitrary(),
            value => UnityCatalogIdentifier.IsValid(value))
        .Check(Config.QuickThrowOnFailure);

    /// <summary>
    /// The security property, and the one that matters: no hostile character survives anywhere in
    /// an otherwise valid identifier — start, middle, or end.
    /// </summary>
    [Fact]
    public void One_hostile_character_anywhere_is_enough_to_reject_it() =>
        Prop.ForAll(
            ValidIdentifier().Where(v => v.Length is > 0 and <= 40).ToArbitrary(),
            Gen.Elements(Hostile).ToArbitrary(),
            Gen.Choose(0, 40).ToArbitrary(),
            (identifier, hostile, offset) =>
            {
                var at = offset % (identifier.Length + 1);
                var spliced = identifier.Insert(at, hostile.ToString());
                return !UnityCatalogIdentifier.IsValid(spliced);
            })
        .Check(Config.QuickThrowOnFailure);

    /// <summary>
    /// Every hostile character, appended. Deterministic on purpose: the splice property above puts
    /// the character at a random offset, so it lands at the *end* about one run in twenty, and the
    /// end is exactly where the documented bug lived. Mutating the guard back to a `$` anchor left
    /// the properties green and this red, which is why both exist.
    /// </summary>
    [Theory]
    [InlineData("\n")]     // the one the `\z` anchor exists for
    [InlineData("\r")]
    [InlineData("\r\n")]
    [InlineData("\t")]
    [InlineData("`")]
    [InlineData("\'")]
    [InlineData("\"")]
    [InlineData(";")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("\0")]
    public void A_hostile_character_appended_to_a_valid_identifier_is_rejected(string suffix)
    {
        // Arrange — the base is unambiguously valid, so a rejection can only be the suffix.
        const string valid = "tenant_a";
        UnityCatalogIdentifier.IsValid(valid).ShouldBeTrue();

        // Act, Assert
        UnityCatalogIdentifier.IsValid(valid + suffix).ShouldBeFalse();
    }

    /// <summary>
    /// Anything accepted is made only of characters that cannot terminate an identifier. Stated
    /// over arbitrary strings as well, because a guard that accepted something exotic — a surrogate
    /// pair, a right-to-left override — would be caught here rather than by the near-miss generator.
    /// </summary>
    [Fact]
    public void An_accepted_identifier_contains_only_safe_characters() =>
        Prop.ForAll<string>(value =>
            !UnityCatalogIdentifier.IsValid(value)
            || value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
        .Check(Config.QuickThrowOnFailure);

    /// <summary>The 63-character ceiling Unity Catalog imposes, checked at the boundary.</summary>
    [Fact]
    public void An_identifier_past_the_ceiling_is_rejected() =>
        Prop.ForAll(
            Gen.Choose(64, 200).ToArbitrary(),
            length => !UnityCatalogIdentifier.IsValid("a" + new string('b', length - 1)))
        .Check(Config.QuickThrowOnFailure);

    /// <summary>
    /// The two entry points cannot disagree. <c>Validate</c> is what callers hit; <c>IsValid</c> is
    /// what the properties above characterise, and a divergence would make them describe a function
    /// nothing calls.
    /// </summary>
    [Fact]
    public void Validate_throws_exactly_when_IsValid_is_false() =>
        Prop.ForAll<string>(value =>
        {
            var threw = false;
            try
            {
                UnityCatalogIdentifier.Validate(value, nameof(value));
            }
            catch (ArgumentException)
            {
                threw = true;
            }

            return threw != UnityCatalogIdentifier.IsValid(value);
        })
        .Check(Config.QuickThrowOnFailure);

    /// <summary>
    /// Every tenant gets a usable schema name. Provisioning derives this rather than looking it up,
    /// so a tenant id that produced an invalid identifier would fail at provisioning time against a
    /// real workspace — the most expensive place to find out.
    /// </summary>
    [Fact]
    public void Every_tenant_id_derives_a_valid_schema_name() =>
        Prop.ForAll<Guid>(id =>
            UnityCatalogIdentifier.IsValid(UnityCatalogIdentifier.SchemaForTenant(new TenantId(id))))
        .Check(Config.QuickThrowOnFailure);

    /// <summary>
    /// Distinct tenants never share a schema. Two tenants in one schema is the failure this project
    /// exists to prevent, and it would arrive as data under the wrong organisation rather than as
    /// an error.
    /// </summary>
    [Fact]
    public void Distinct_tenants_never_share_a_schema() =>
        Prop.ForAll<Guid, Guid>((left, right) =>
            left == right
            || UnityCatalogIdentifier.SchemaForTenant(new TenantId(left))
               != UnityCatalogIdentifier.SchemaForTenant(new TenantId(right)))
        .Check(Config.QuickThrowOnFailure);
}
