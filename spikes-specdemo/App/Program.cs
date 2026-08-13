using Spec.Generated;

// Nothing here was hand-written except this file and the yaml.
Report("valid",   new Pet { Name = "Rex", Sku = "ABC", Slug = "rex-the-dog" });
Report("bad sku", new Pet { Name = "Rex", Sku = "abc", Slug = "rex-the-dog" });
Report("missing", new Pet { Name = "   ", Sku = "ABC", Slug = "rex-the-dog" });
Report("embedded", new Pet { Name = "Rex", Sku = "xABCx", Slug = "rex-the-dog" });

static void Report(string label, Pet pet) {
    var errors = PetValidator.Validate(pet);
    Console.WriteLine($"  {label,-9} -> {(errors.Count == 0 ? "ok" : string.Join(", ", errors))}");
}

namespace Spec.Generated {
    public sealed class Pet {
        public string? Name { get; init; }
        public string? Sku { get; init; }
        public string? Slug { get; init; }
    }
}
