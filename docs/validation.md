# Validation

The `Namotion.Interceptor.Validation` package provides automatic property validation using Data Annotations or custom validators. Validation runs when properties are written, throwing a `ValidationException` if the new value is invalid.

## What is not validated

Every property write is validated except one: a value a source confirmed during a transaction commit (`Confirmed`). That value is the model's own. It already passed validation when the transaction captured it, so re-checking is redundant, and rejecting it now would make the commit revert a write the source has already accepted, which other subscribers of that source observe as a value flap.

The same applies when a commit rolls those changes back, since a rollback carries the origin of the change it reverts. One consequence follows: a partially failed `BestEffort` commit can leave the model in a state its own validators would reject, because the changes a source accepted are applied without being re-checked against the ones that failed. That is the intended trade.

**Everything else keeps its veto, including values a source sends inbound.** A validator is a statement about your model's invariants, and they hold whatever the write's provenance. The cost is that rejecting an inbound value leaves the model holding the old value while the source holds the new one, and nothing reconciles that today, so the disagreement lasts until the property changes again or the connection reloads. Expect a rejected inbound value to show up as a model that disagrees with its source.

The exemption uses the write's *effective* origin rather than the origin the caller declared, so a confirmed value that an `OnChanging` hook transformed on the way in is no longer what the source confirmed, is therefore locally computed, and is validated. That is defensive rather than a case you should expect to hit: the transaction captured the post-hook value, so an idempotent hook, which is what the transform contract requires, re-computes the same value and the exemption still applies.

A derived property that recalculates because of an inbound write is a separate, locally computed write, so it runs validators too. That is not a veto: a derived property's value is stored before its change is published, so a validator that throws there does not reject the value, it only suppresses that change notification and the remainder of the cascade, while the getter keeps returning the new value. The exception does still reach whoever performed the triggering write, so that write appears to fail even though both values landed. Do not rely on a validator to guard a derived property.

When a validator rejects an inbound write, the connector reports the exception, but what it does next differs: connectors that apply change by change, such as the OPC UA subscription and polling paths, continue with the next change, while connectors that apply a whole graph update abandon the remainder of that update.

Note that this describes the write interceptor. Code that resolves `IPropertyValidator` and invokes it directly, such as the ASP.NET Core update endpoint, is validating local input before writing and is unaffected.

## Setup

For standard Data Annotation validation (most common):

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithDataAnnotationValidation();
```

If you only need custom validators without Data Annotations:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyValidation();
```

## Data Annotation Validation

Use standard .NET Data Annotation attributes on your properties:

```csharp
[InterceptorSubject]
public partial class Person
{
    [Required]
    [MaxLength(50)]
    public partial string FirstName { get; set; }

    [Range(0, 150)]
    public partial int Age { get; set; }

    [EmailAddress]
    public partial string? Email { get; set; }
}
```

Validation runs automatically on property writes:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithDataAnnotationValidation();

var person = new Person(context);

person.FirstName = "John";  // OK
person.Age = 25;            // OK

person.FirstName = "This name is way too long and exceeds the maximum length";
// Throws ValidationException

person.Age = -5;
// Throws ValidationException: The field Age must be between 0 and 150.
```

The original value is preserved when validation fails:

```csharp
person.FirstName = "Rico";  // OK

try
{
    person.FirstName = "This is too long";
}
catch (ValidationException)
{
    // Validation failed
}

Console.WriteLine(person.FirstName);  // Still "Rico"
```

## Custom Validators

For validation logic beyond Data Annotations, implement `IPropertyValidator`:

```csharp
public class NoSwearWordsValidator : IPropertyValidator
{
    private static readonly string[] BadWords = ["bad", "words"];

    public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
    {
        if (context.Value is not string text)
        {
            return [];
        }

        List<ValidationResult>? results = null;
        foreach (var word in BadWords)
        {
            if (text.Contains(word, StringComparison.OrdinalIgnoreCase))
            {
                results ??= [];
                results.Add(new ValidationResult(
                    $"Property '{context.Property.Name}' contains prohibited word: {word}"));
            }
        }

        return results ?? [];
    }
}
```

The `PropertyValidationContext` carries the property, the new value, and the effective `Origin` of the write, which is either `Local` or `FromSource`. It is never `Confirmed`, because a confirmed value is not validated. See [what is not validated](#what-is-not-validated). Because the context is passed by `in`, implementations must return a collection instead of using `yield`.

Register your custom validator:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithPropertyValidation()
    .WithService<IPropertyValidator>(() => new NoSwearWordsValidator());
```

Multiple validators can be registered and all will run. Errors from all validators are combined into a single `ValidationException`.

### Use Cases for Custom Validators

- **Cross-property validation**: Access other properties via `context.Property.Subject`
- **External validation**: Check against databases, APIs, or configuration
- **Complex business rules**: Validation logic that doesn't fit in attributes
- **Conditional validation**: Rules that depend on subject state

## Combining Data Annotations and Custom Validators

Both can be used together:

```csharp
var context = InterceptorSubjectContext
    .Create()
    .WithDataAnnotationValidation()  // Includes WithPropertyValidation()
    .WithService<IPropertyValidator>(() => new MyCustomValidator());
```

Data Annotations and custom validators all run on each validated property write, which is every write except the one skipped above. If any validator returns errors, the write is rejected.

## Dynamic Properties

Note that .NET's `Validator.TryValidateProperty` does not support dynamically added properties (via `Namotion.Interceptor.Dynamic` or registry). Data Annotation validation is automatically skipped for dynamic properties. Use custom validators if you need validation on dynamic properties.
