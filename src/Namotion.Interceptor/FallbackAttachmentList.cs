namespace Namotion.Interceptor;

/// <summary>
/// The singly linked list of <see cref="FallbackAttachment"/> records a context keeps, threaded
/// through <see cref="FallbackAttachment.Next"/> so a context that registers no fallback edge pays
/// nothing but a null field.
/// </summary>
/// <remarks>
/// Mutating operations take the head by reference. None of them locks: the caller holds the
/// owning context's mutation lock, which is what keeps a record atomic with the edge it owns.
/// </remarks>
internal static class FallbackAttachmentList
{
    internal static void Link(ref FallbackAttachment? head, FallbackAttachment attachment)
    {
        attachment.Next = head;
        head = attachment;
    }

    /// <summary>Returns the record owning the edge to the given context, or null when there is none.</summary>
    internal static FallbackAttachment? Find(FallbackAttachment? head, InterceptorSubjectContext context)
    {
        var attachment = head;
        while (attachment is not null && !ReferenceEquals(attachment.Context, context))
        {
            attachment = attachment.Next;
        }

        return attachment;
    }

    internal static void Unlink(ref FallbackAttachment? head, FallbackAttachment attachment)
    {
        if (ReferenceEquals(head, attachment))
        {
            head = attachment.Next;
            return;
        }

        var previous = head;
        while (previous is not null && !ReferenceEquals(previous.Next, attachment))
        {
            previous = previous.Next;
        }

        if (previous is not null)
        {
            previous.Next = attachment.Next;
        }
    }
}
