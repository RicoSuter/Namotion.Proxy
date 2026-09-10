using System.ComponentModel.DataAnnotations;

namespace Namotion.Interceptor.Validation.Tests;

/// <summary>
/// The Validation configuration extensions are idempotent for their default services, and the
/// singleton contracts on the defaults turn a competing registration into an error while ordinary
/// <see cref="IPropertyValidator"/> registrations stay plural.
/// </summary>
public class ValidationConfigurationTests
{
    [Fact]
    public void WhenPropertyValidationIsConfiguredRepeatedly_ThenOneInterceptorIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithPropertyValidation()
            .WithPropertyValidation();

        // Assert
        Assert.Single(context.GetServices<ValidationInterceptor>());
    }

    [Fact]
    public void WhenDataAnnotationValidationIsConfiguredRepeatedly_ThenOneValidatorIsRegistered()
    {
        // Arrange & Act
        var context = InterceptorSubjectContext
            .Create()
            .WithDataAnnotationValidation()
            .WithDataAnnotationValidation();

        // Assert
        Assert.Single(context.GetServices<ValidationInterceptor>());
        Assert.Single(context.GetServices<IPropertyValidator>());
    }

    [Fact]
    public void WhenTheValidationInterceptorContractIsClaimed_ThenWithDataAnnotationValidationPublishesNoValidator()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new ValidationInterceptorSlotClaimant());

        // Act & Assert: the conflict fires while establishing the interceptor, before the
        // annotations validator is published.
        var exception = Assert.Throws<InvalidOperationException>(() => context.WithDataAnnotationValidation());
        Assert.Contains("singleton contract", exception.Message);
        Assert.Empty(context.GetServices<IPropertyValidator>());
    }

    [Fact]
    public void WhenTheDataAnnotationsValidatorContractIsClaimed_ThenTheInterceptorStaysAndNoValidatorIsPublished()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService(new DataAnnotationsValidatorSlotClaimant());

        // Act & Assert: the interceptor is the extension's dependency and is established first, so
        // it survives the conflict; the extension's own validator must not.
        Assert.Throws<InvalidOperationException>(() => context.WithDataAnnotationValidation());
        Assert.NotNull(context.TryGetService<ValidationInterceptor>());
        Assert.Empty(context.GetServices<IPropertyValidator>());
    }

    [Fact]
    public void WhenACustomValidatorIsRegistered_ThenDataAnnotationValidationAddsTheDefaultBesideIt()
    {
        // Arrange
        var context = InterceptorSubjectContext.Create();
        context.AddService<IPropertyValidator>(new CustomValidator());

        // Act
        context.WithDataAnnotationValidation();

        // Assert: validators are an ordinary plural chain; only the default implementations carry
        // singleton contracts.
        Assert.Equal(2, context.GetServices<IPropertyValidator>().Length);
    }

    private sealed class ValidationInterceptorSlotClaimant : ISingletonContextService<ValidationInterceptor>;

    private sealed class DataAnnotationsValidatorSlotClaimant : ISingletonContextService<DataAnnotationsValidator>;

    private sealed class CustomValidator : IPropertyValidator
    {
        public IEnumerable<ValidationResult> Validate<TProperty>(in PropertyValidationContext<TProperty> context)
            => [];
    }
}
