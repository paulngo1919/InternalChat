using InternalChat.Domain.Common;

namespace InternalChat.Domain.Messages;

/// <summary>
/// The text of a message: 1 to 8,000 characters after trimming (FR-019).
/// </summary>
/// <remarks>
/// <para>
/// A value object rather than a validated <c>string</c> parameter, because there are several ways a
/// body enters the system — send, edit, the seeder — and a type that cannot hold an invalid value
/// makes the limit unbreakable instead of checked at each entry point.
/// </para>
/// <para>
/// <b>Trimming, but not normalising.</b> Leading and trailing whitespace is noise a client
/// contributes by accident. Internal whitespace is content: collapsing it would silently rewrite a
/// pasted code snippet or an aligned table, which people notice and cannot explain.
/// </para>
/// </remarks>
public sealed class MessageBody : ValueObject
{
    /// <summary>
    /// Longest permitted body, in characters.
    /// </summary>
    /// <remarks>
    /// Matches the <c>maxLength</c> in <c>contracts/openapi.yaml</c> and the CHECK constraint in
    /// <c>data-model.md</c>. All three exist deliberately: the contract tells a client what to
    /// expect, this gives a usable error, and the constraint is what holds if either is bypassed.
    /// </remarks>
    public const int MaximumLength = 8000;

    private MessageBody(string value) => Value = value;

    /// <summary>The trimmed text.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a body, trimming first.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The text is empty, whitespace-only, or longer than <see cref="MaximumLength"/>.
    /// </exception>
    public static MessageBody Create(string? text)
    {
        // Trim BEFORE measuring. Measuring first would refuse an 8,000-character body that happened
        // to arrive with a trailing newline, and tell the sender it was too long when it was not.
        string trimmed = text?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            throw new ArgumentException(
                "A message body cannot be empty or whitespace only (FR-019).",
                nameof(text));
        }

        if (trimmed.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"A message body is at most {MaximumLength} characters; this one is {trimmed.Length}.",
                nameof(text));
        }

        return new MessageBody(trimmed);
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Value;
    }

    /// <inheritdoc />
    public override string ToString() => Value;
}
