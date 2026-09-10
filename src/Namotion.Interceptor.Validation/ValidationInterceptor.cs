using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using System.Text;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Interceptor that validates property values using registered validators before writing.
/// Runs before the transaction interceptor to validate during both capture and commit phases.
/// </summary>
[RunsBefore(typeof(SubjectTransactionInterceptor))]
public class ValidationInterceptor : IWriteInterceptor,
    ISingletonContextService<ValidationInterceptor>
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // The chain executing this write was resolved from the subject's attached context, so that
        // context's validators are the ones that apply; an unattached subject runs no chain and is
        // never validated.
        var validators = context.Property.Subject.TryGetContext()?.GetServices<IPropertyValidator>()
            ?? ImmutableArray<IPropertyValidator>.Empty;

        var validationContext = new PropertyValidationContext<TProperty>(context.Property, context.NewValue, context.Origin);

        List<ValidationResult>? additionalErrors = null;
        foreach (var validator in validators)
        {
            foreach (var error in validator.Validate(in validationContext))
            {
                additionalErrors ??= [];
                additionalErrors.Add(error);
            }
        }

        if (additionalErrors is not null)
        {
            var sb = new StringBuilder();
            foreach (var error in additionalErrors)
            {
                sb.Append('\n');
                sb.Append(error.ErrorMessage);
            }
            
            throw new ValidationException(sb.ToString());
        }

        next(ref context);
    }
}
