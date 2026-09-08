using System.ComponentModel.DataAnnotations;
using Namotion.Interceptor.Tracking;
using Namotion.Interceptor.Tracking.Change;
using Namotion.Interceptor.Validation.Tests.Models;

namespace Namotion.Interceptor.Validation.Tests;

public class ValidationInterceptorTests
{
    [Fact]
    public void ShouldValidateProperty()
    {
        // Arrange
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithDataAnnotationValidation();

        // Act
        var person = new Person(context)
        {
            FirstName = "Rico" // allowed
        };

        // Assert
        Assert.Throws<ValidationException>(() =>
        {
            person.FirstName = "Suter"; // not allowed
        });
        Assert.Equal("Rico", person.FirstName);
    }

    [Fact]
    public void WhenOriginIsLocal_ThenValidatorIsInvokedAndRejects()
    {
        // Arrange
        var validator = new RecordingValidator(nameof(Person.LastName));
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithFullPropertyTracking()
            .WithService<IPropertyValidator>(() => validator);

        var person = new Person(context);

        // Act & Assert
        Assert.Throws<ValidationException>(() => person.LastName = "anything");
        Assert.Null(person.LastName);
        Assert.Equal([ChangeOriginKind.Local], validator.SeenOrigins);
    }

    [Fact]
    public void WhenSourceWriteTriggersDerivedRecalculation_ThenTheDerivedWriteIsStillValidated()
    {
        // Arrange: FullName depends on LastName, and its recalculation is a separate locally computed
        // write, so it reports Local rather than inheriting the inbound origin.
        var validator = new RecordingValidator(nameof(Person.FullName));
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithFullPropertyTracking()
            .WithService<IPropertyValidator>(() => validator);

        var person = new Person(context);
        var source = new object();

        // Act & Assert
        Assert.Throws<ValidationException>(() =>
            new PropertyReference(person, nameof(Person.LastName))
                .SetValueFromSource(source, null, null, "anything"));

        Assert.Equal([ChangeOriginKind.Local], validator.SeenOrigins);

        // The trigger write itself was not vetoed by this validator, which is scoped to FullName.
        Assert.Equal("anything", person.LastName);
    }

    [Fact]
    public void WhenOriginIsFromSource_ThenValidationStillApplies()
    {
        // Arrange: an inbound value from a source is validated like any other write. Rejecting it
        // leaves the model disagreeing with its source, which is reported rather than repaired.
        var validator = new RecordingValidator(nameof(Person.LastName));
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithFullPropertyTracking()
            .WithService<IPropertyValidator>(() => validator);

        var person = new Person(context);
        var peer = new object();

        // Act & Assert
        Assert.Throws<ValidationException>(() =>
            new PropertyReference(person, nameof(Person.LastName))
                .SetValueFromSource(peer, null, null, "anything"));

        Assert.Null(person.LastName);
        Assert.Equal([ChangeOriginKind.FromSource], validator.SeenOrigins);
    }

    /// <summary>
    /// Rejects every write to one property and records the origin it was handed for that property, so
    /// a test can assert which origin the interceptor reported as well as that the write was rejected.
    /// Scoped to a single property because a write also triggers derived recalculations, which are
    /// local by design and would otherwise register here.
    /// </summary>
    private sealed class RecordingValidator(string propertyName) : IPropertyValidator
    {
        public List<ChangeOriginKind> SeenOrigins { get; } = [];

        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
        {
            if (context.Property.Name != propertyName)
            {
                return [];
            }

            SeenOrigins.Add(context.Origin.Kind);
            return [new ValidationResult("Rejected unconditionally.")];
        }
    }
}
