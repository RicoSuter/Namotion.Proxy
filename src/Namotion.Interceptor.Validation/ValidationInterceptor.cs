using System.ComponentModel.DataAnnotations;
using System.Text;
using Namotion.Interceptor.Attributes;
using Namotion.Interceptor.Interceptors;
using Namotion.Interceptor.Tracking.Transactions;

namespace Namotion.Interceptor.Validation;

/// <summary>
/// Interceptor that validates property values using registered validators before writing.
/// Every write is validated except one: a value a source confirmed during a transaction commit
/// (<see cref="ChangeOriginKind.Confirmed"/>) is applied unvalidated, because it is the model's own
/// value returning after a source accepted it. It already passed validation when the transaction
/// captured it, so re-checking is redundant, and a rejection would make the commit revert a write
/// the source has taken.
/// Inbound values from a source are validated like any other write. A rejection leaves the model
/// disagreeing with its source, which is reported rather than repaired; see the validation
/// documentation.
/// Runs before the transaction interceptor so a local write is validated at capture as well as when
/// the commit replays it.
/// </summary>
[RunsBefore(typeof(SubjectTransactionInterceptor))]
public class ValidationInterceptor : IWriteInterceptor
{
    /// <inheritdoc />
    public void WriteProperty<TProperty>(ref PropertyWriteContext<TProperty> context, WriteInterceptionDelegate<TProperty> next)
    {
        // A confirmed value is the model's own value returning after a source accepted it, so it has
        // already been validated once at capture and rejecting it now would revert an accepted write.
        // The EFFECTIVE origin, not context.Origin: if a hook transformed the value on the way in, the
        // stored value is no longer what the source confirmed, so it is locally computed and validated.
        // Placed before validator resolution so a confirmed replay does no validation work at all.
        var origin = context.GetEffectiveOrigin();
        if (origin.Kind == ChangeOriginKind.Confirmed)
        {
            next(ref context);
            return;
        }

        var validators = context.Property.Subject.Context.GetServices<IPropertyValidator>();

        // The effective origin, not context.Origin: a hook-transformed inbound value is reported as
        // Local, which is what it is, rather than as the origin the caller declared.
        var validationContext = new PropertyValidationContext<TProperty>(
            context.Property, context.NewValue, origin);

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
