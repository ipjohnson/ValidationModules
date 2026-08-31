using ValidationModules.Naming;
using ValidationModules.Rules;
using Xunit;

namespace ValidationModules.Runtime.Tests;

/// <summary>
/// Pins the selector and predicate text transforms.
/// </summary>
/// <remarks>
/// <see cref="RuleText"/> is compiled into the source generator as well as into the runtime, so
/// these assertions pin the transforms for both. That is the point of sharing the file: the message
/// and the code an <c>Ensure</c> bakes into generated source come from exactly one render, and two
/// implementations would agree only until someone edited one of them.
/// </remarks>
public class RuleTextTests {

    private static string Name(string clr) => CamelCaseFieldNamer.Instance.ToFieldName(clr);

    [Theory]
    [InlineData("x => x.Age", "Age")]
    [InlineData("pet => pet.Age", "Age")]
    [InlineData("(x) => x.Age", "Age")]
    [InlineData("(Pet x) => x.Age", "Age")]
    [InlineData("static x => x.Age", "Age")]
    [InlineData("x =>\n    x.Age", "Age")]
    public void PropertyOfSelector_ReadsTheProperty(string selector, string expected) {
        Assert.Equal(expected, RuleText.PropertyOfSelector(selector));
    }

    [Fact]
    public void PropertyOfSelector_OnANestedPath_TakesTheOutermostProperty() {
        // The error is pathed against the property this type owns; "postalCode" belongs to Address.
        Assert.Equal("Home", RuleText.PropertyOfSelector("x => x.Home.PostalCode"));
    }

    [Theory]
    [InlineData("x => x.Age + 1")]
    [InlineData("x => 3")]
    [InlineData("x => Other.Age")]
    [InlineData(null)]
    public void PropertyOfSelector_OnSomethingThatIsNotAPath_IsNull(string? selector) {
        // VM3007's case. Naming the error "age" for "x => x.Age + 1" would be a guess.
        Assert.Null(RuleText.PropertyOfSelector(selector));
    }

    [Theory]
    [InlineData("x => x.Start < x.End", "Start")]
    [InlineData("x => x.Age is >= 0 and <= 30", "Age")]
    [InlineData("x => !string.IsNullOrWhiteSpace(x.Name)", "Name")]
    [InlineData("x => Patterns.Sku().IsMatch(x.Sku)", "Sku")]
    public void AnchorOfPredicate_TakesTheFirstMemberOfTheParameter(string predicate, string expected) {
        Assert.Equal(expected, RuleText.AnchorOfPredicate(predicate));
    }

    [Fact]
    public void AnchorOfPredicate_WhenThePredicateNeverReadsItsParameter_IsNull() {
        // VM3102's case: nothing to anchor to and no field: supplied.
        Assert.Null(RuleText.AnchorOfPredicate("x => Constants.Enabled"));
    }

    [Fact]
    public void AnchorOfPredicate_DoesNotMistakeAMemberNamedLikeTheParameter() {
        // "other.x" is a member of something else. Anchoring to "Name" here would be wrong twice:
        // wrong property, and a property this type does not have.
        Assert.Equal("Real", RuleText.AnchorOfPredicate("x => Other.x.Name == x.Real"));
    }

    [Theory]
    [InlineData("x => x.Start < x.End", "start < end.")]
    [InlineData("x => x.Age is >= 0 and <= 30", "age is >= 0 and <= 30.")]
    [InlineData("x => x.Name.Length is >= 1 and <= 100", "name.Length is >= 1 and <= 100.")]
    [InlineData("x => !string.IsNullOrWhiteSpace(x.Name)", "!string.IsNullOrWhiteSpace(name).")]
    [InlineData("x => Patterns.Sku().IsMatch(x.Sku)", "Patterns.Sku().IsMatch(sku).")]
    public void RenderPredicate_StripsTheParameterAndNamesTheMembers(string predicate, string expected) {
        Assert.Equal(expected, RuleText.RenderPredicate(predicate, Name));
    }

    [Fact]
    public void RenderPredicate_NormalizesWhitespace() {
        // The runtime gets this text from the compiler and the generator reads it off a syntax node.
        // Both are the expression's source span, but interior trivia in a multi-line lambda is where
        // they would part company - and a reformatted lambda must not change a message either way.
        Assert.Equal(
            RuleText.RenderPredicate("x => x.Start < x.End", Name),
            RuleText.RenderPredicate("x =>\n        x.Start\n            < x.End", Name));
    }

    [Fact]
    public void RenderPredicate_LeavesStringLiteralsAlone() {
        // "x." inside a literal is text, not a member access, and collapsing its spaces would edit
        // data the author wrote deliberately.
        Assert.Equal(
            "sku.StartsWith(\"x.  INT\").",
            RuleText.RenderPredicate("x => x.Sku.StartsWith(\"x.  INT\")", Name));
    }

    [Fact]
    public void RenderPredicate_DoesNotDoubleThePeriod() {
        Assert.Equal("name.Length > 3.", RuleText.RenderPredicate("x => x.Name.Length > 3.", Name));
    }

    [Theory]
    [InlineData("x => x.Start < x.End", "start_less_than_end")]
    [InlineData("x => x.Total <= x.CreditLimit", "total_less_than_or_equal_credit_limit")]
    [InlineData("x => x.Start > x.End", "start_greater_than_end")]
    [InlineData("x => x.Count >= x.Minimum", "count_greater_than_or_equal_minimum")]
    [InlineData("x => x.Status == x.Wanted", "status_equal_wanted")]
    [InlineData("x => x.CustomsCode != null", "customs_code_is_not_null")]
    [InlineData("x => x.Paid && x.Shipped", "paid_and_shipped")]
    [InlineData("x => x.Paid || x.Waived", "paid_or_waived")]
    [InlineData("x => !x.Cancelled", "not_cancelled")]
    public void CodeOfPredicate_SpellsTheOperator(string predicate, string expected) {
        Assert.Equal(expected, RuleText.CodeOfPredicate(predicate, Name));
    }

    [Fact]
    public void CodeOfPredicate_WideningABoundMovesTheCode() {
        // The whole point. A key that survived this edit would assert that nothing happened, and a
        // translation carried across it would be quietly wrong.
        Assert.NotEqual(
            RuleText.CodeOfPredicate("x => x.Start < x.End", Name),
            RuleText.CodeOfPredicate("x => x.Start <= x.End", Name));
    }

    [Fact]
    public void CodeOfPredicate_FollowsTheWireNameRatherThanTheClrName() {
        // The reason this derives from the render. A property renamed in C# behind a pinned
        // [JsonPropertyName] keeps its wire name, so neither the message nor the code moves.
        static string Pinned(string clr) => clr == "BeginsAt" || clr == "Start" ? "start" : Name(clr);

        Assert.Equal(
            RuleText.CodeOfPredicate("x => x.Start < x.End", Pinned),
            RuleText.CodeOfPredicate("x => x.BeginsAt < x.End", Pinned));
    }

    [Fact]
    public void CodeOfPredicate_ReadsALiteralAsPartOfTheRule() {
        // Comparing against a different value is a different rule, so the code moves with it.
        Assert.Equal("status_equal_active", RuleText.CodeOfPredicate("x => x.Status == \"active\"", Name));
        Assert.Equal("status_equal_pending", RuleText.CodeOfPredicate("x => x.Status == \"pending\"", Name));
    }

    [Theory]
    [InlineData("x => x.Name.Length > 3", "name_length_greater_than_3")]
    [InlineData("x => !string.IsNullOrWhiteSpace(x.Name)", "name_is_not_null_or_blank")]
    [InlineData("x => x.HTTPStatus == 200", "http_status_equal_200")]
    [InlineData("x => x.Total * 2 > x.Limit", "total_times_2_greater_than_limit")]
    public void CodeOfPredicate_SplitsHumpsAndAcronymsAndDropsStructure(string predicate, string expected) {
        Assert.Equal(expected, RuleText.CodeOfPredicate(predicate, Name));
    }

    [Fact]
    public void CodeOfPredicate_IsUnaffectedByFormatting() {
        Assert.Equal(
            RuleText.CodeOfPredicate("x => x.Start < x.End", Name),
            RuleText.CodeOfPredicate("x =>\n        x.Start\n            < x.End", Name));
    }

    [Theory]
    [InlineData("x => string.IsNullOrEmpty(x.Name)", "name_is_null_or_empty")]
    [InlineData("x => !string.IsNullOrEmpty(x.Name)", "name_is_not_null_or_empty")]
    [InlineData("x => string.IsNullOrWhiteSpace(x.Name)", "name_is_null_or_blank")]
    [InlineData("x => x.Name == null", "name_is_null")]
    [InlineData("x => x.Items.Count == 0", "items_is_empty")]
    [InlineData("x => x.Items.Length > 0", "items_is_not_empty")]
    public void CodeOfPredicate_NamesAnIdiomForWhatItAsserts(string predicate, string expected) {
        // The subject comes first even though the tokens do not, which is the readability the
        // table exists for: not_string_is_null_or_white_space_name said the same thing far worse.
        Assert.Equal(expected, RuleText.CodeOfPredicate(predicate, Name));
    }

    [Fact]
    public void CodeOfPredicate_TwoSpellingsOfOneAssertion_Agree() {
        // Collapsing these is the point of the table, not a collision.
        Assert.Equal(
            RuleText.CodeOfPredicate("x => x.Name == null", Name),
            RuleText.CodeOfPredicate("x => x.Name is null", Name));
    }

    [Fact]
    public void CodeOfPredicate_AnIdiomInsideALargerRule_KeepsTheRest() {
        // The emptiness idiom only fires when the comparison is the whole rule; a count being
        // used for something else still reads as a count.
        Assert.Equal("paid_and_name_is_not_null_or_empty",
            RuleText.CodeOfPredicate("x => x.Paid && !string.IsNullOrEmpty(x.Name)", Name));

        Assert.Equal("items_count_greater_than_0_and_paid",
            RuleText.CodeOfPredicate("x => x.Items.Count > 0 && x.Paid", Name));
    }

    [Fact]
    public void CodeOfPredicate_DropsLambdaParameters() {
        // A parameter name is not part of the rule, so renaming one must not move the code.
        Assert.Equal(
            RuleText.CodeOfPredicate("x => x.Lines.Sum(l => l.Price) > 0", Name),
            RuleText.CodeOfPredicate("x => x.Lines.Sum(line => line.Price) > 0", Name));
    }

    [Fact]
    public void CodeOfPredicate_KeepsPrecedenceAndPunctuation() {
        // Both pairs used to collide, which is two different rules under one wire code.
        Assert.NotEqual(
            RuleText.CodeOfPredicate("x => !(x.Paid && x.Shipped)", Name),
            RuleText.CodeOfPredicate("x => !x.Paid && x.Shipped", Name));

        Assert.NotEqual(
            RuleText.CodeOfPredicate("x => x.Email.Contains(\"@\")", Name),
            RuleText.CodeOfPredicate("x => x.Email.Contains(\".\")", Name));
    }

    [Theory]
    [InlineData("@", "at")]
    [InlineData(".", "dot")]
    [InlineData("-", "dash")]
    [InlineData("_", "underscore")]
    [InlineData("/", "slash")]
    [InlineData(":", "colon")]
    [InlineData(";", "semicolon")]
    [InlineData(",", "comma")]
    [InlineData("+", "plus")]
    [InlineData("*", "star")]
    [InlineData("#", "hash")]
    [InlineData("%", "percent")]
    [InlineData("&", "amp")]
    [InlineData("?", "question")]
    [InlineData("!", "bang")]
    [InlineData("=", "equals")]
    [InlineData("|", "pipe")]
    [InlineData("^", "caret")]
    [InlineData("~", "tilde")]
    [InlineData("$", "dollar")]
    [InlineData("'", "quote")]
    [InlineData("(", "lparen")]
    [InlineData(")", "rparen")]
    [InlineData("[", "lbracket")]
    [InlineData("]", "rbracket")]
    [InlineData("{", "lbrace")]
    [InlineData("}", "rbrace")]
    [InlineData("<", "lt")]
    [InlineData(">", "gt")]
    [InlineData("\u00a7", "cpa7")]
    public void CodeOfPredicate_NamesPunctuationInsideALiteral(string literal, string expected) {
        // Every one of these is a wire contract. Dropping punctuation collided Contains("@") with
        // Contains("."), so each character has to reach the code as something.
        Assert.Equal(
            "sku_contains_" + expected,
            RuleText.CodeOfPredicate($"x => x.Sku.Contains(\"{literal}\")", Name));
    }

    [Fact]
    public void CodeOfPredicate_NamesABackslashInALiteral() {
        // Separate from the theory because the escape has to survive being read as source. The
        // text carries both characters of the escape, so both are named.
        Assert.Equal(
            "path_contains_backslash_backslash",
            RuleText.CodeOfPredicate("x => x.Path.Contains(\"\\\\\")", Name));
    }

    [Fact]
    public void CodeOfPredicate_IgnoresTheNullForgivingOperator() {
        // "!" after a value tells the compiler what the author knows. Reading it as a negation
        // made x.Name!.Length and x.Name.Length two rules, and claimed a "not" that is not there.
        Assert.Equal(
            RuleText.CodeOfPredicate("x => x.Name.Length > 3", Name),
            RuleText.CodeOfPredicate("x => x.Name!.Length > 3", Name));

        // The prefix form still negates.
        Assert.Equal("not_cancelled", RuleText.CodeOfPredicate("x => !x.Cancelled", Name));
    }

    [Fact]
    public void CodeOfPredicate_ReadsATypedLambdaHead() {
        // "(Line l) => …" names a type then a parameter; the parameter is the last identifier, and
        // neither belongs in the code.
        Assert.Equal(
            RuleText.CodeOfPredicate("x => x.Lines.Any(l => l.Sku == null)", Name),
            RuleText.CodeOfPredicate("x => x.Lines.Any((Line l) => l.Sku == null)", Name));
    }

    [Fact]
    public void CodeOfPredicate_DoesNotMistakeAParenthesisInsideALiteralForTheEndOfAnIdiom() {
        // MatchingParenthesis skips literals, so the argument is not cut short at that ')' and the
        // idiom still closes on the real one.
        //
        // Punctuation inside an idiom's argument is dropped rather than named, unlike a literal
        // anywhere else: the argument is read as a path, which is what an argument to a null check
        // is in every case worth optimising for. Two arguments differing only in the punctuation of
        // an embedded literal would share a code, which is accepted rather than unnoticed.
        Assert.Equal(
            "name_is_null_or_empty",
            RuleText.CodeOfPredicate("x => string.IsNullOrEmpty(x.Name + \")\")", Name));
    }

    [Fact]
    public void CodeOfPredicate_WithNothingDerivable_IsNull() {
        // The caller keeps the generic code rather than emitting an empty one.
        Assert.Null(RuleText.CodeOfPredicate(null, Name));
    }
}
