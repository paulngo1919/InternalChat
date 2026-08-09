using InternalChat.Domain.Common;
using InternalChat.Domain.Employees;

namespace InternalChat.UnitTests.Domain;

/// <summary>
/// T052 — <see cref="Employee"/> invariants.
/// </summary>
/// <remarks>
/// The invariant this suite exists for is the one in the task title: a deactivated employee cannot
/// be added to a conversation. Everything else here protects the timestamps an auditor reads when
/// reconstructing who had access on a given date (SC-021).
/// </remarks>
public sealed class EmployeeTests : UnitTestBase
{
    private static Employee ActiveEmployee(IClock? clock = null) =>
        Employee.Project(
            Guid.CreateVersion7(),
            externalSubject: "dev-an.nguyen",
            displayName: "Nguyễn Thị Vân An",
            email: "an.nguyen@internalchat.local",
            clock ?? FixedClock());

    // -------------------------------------------------------------------------------------------
    // The load-bearing invariant
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Deactivated_employee_cannot_be_added_to_a_conversation()
    {
        Employee employee = ActiveEmployee();
        employee.Deactivate(FixedClock());

        DeactivatedEmployeeException refusal =
            Assert.Throws<DeactivatedEmployeeException>(employee.EnsureCanJoinConversation);

        // The id travels with the exception so the endpoint can say which named member was
        // refused. A 422 that does not name the person is a support ticket.
        Assert.Equal(employee.Id, refusal.EmployeeId);
    }

    [Fact]
    public void Active_employee_can_be_added_to_a_conversation()
    {
        Employee employee = ActiveEmployee();

        // Records the positive case explicitly. Without it, a bug that made the check throw for
        // everyone would still leave the refusal test above passing.
        employee.EnsureCanJoinConversation();

        Assert.True(employee.IsActive);
    }

    [Fact]
    public void Rehired_employee_can_be_added_again()
    {
        Employee employee = ActiveEmployee();
        employee.Deactivate(FixedClock());
        employee.Reactivate(FixedClock("2026-08-02T09:00:00Z"));

        employee.EnsureCanJoinConversation();

        Assert.True(employee.IsActive);
        Assert.Null(employee.DeactivatedAt);
    }

    // -------------------------------------------------------------------------------------------
    // Idempotency — directory sync is an at-least-once consumer
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Repeated_deactivation_does_not_move_the_deactivation_timestamp()
    {
        Employee employee = ActiveEmployee();

        employee.Deactivate(FixedClock("2026-08-01T09:00:00Z"));
        DateTimeOffset first = employee.DeactivatedAt!.Value;

        // A redelivery an hour later. Principle VI guarantees at-least-once, so this happens.
        employee.Deactivate(FixedClock("2026-08-01T10:00:00Z"));

        // If the second delivery moved this forward, an auditor asking when access ended would get
        // the time the queue happened to redeliver rather than the time the employee left.
        Assert.Equal(first, employee.DeactivatedAt);
    }

    [Fact]
    public void Repeated_deactivation_raises_the_event_only_once()
    {
        Employee employee = ActiveEmployee();

        employee.Deactivate(FixedClock());
        employee.Deactivate(FixedClock("2026-08-01T10:00:00Z"));

        // Each event becomes an outbox row and a revocation broadcast. Emitting one per redelivery
        // would turn an at-least-once queue into unbounded fan-out.
        Assert.Single(employee.DomainEvents.OfType<EmployeeDeactivated>());
    }

    [Fact]
    public void Reactivating_an_active_employee_raises_nothing()
    {
        Employee employee = ActiveEmployee();

        employee.Reactivate(FixedClock());

        Assert.Empty(employee.DomainEvents);
    }

    // -------------------------------------------------------------------------------------------
    // Events carry what the revocation path needs
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Deactivation_event_carries_the_external_subject()
    {
        Employee employee = ActiveEmployee();

        employee.Deactivate(FixedClock());

        EmployeeDeactivated raised = Assert.Single(employee.DomainEvents.OfType<EmployeeDeactivated>());

        // The revocation set is keyed by the token's sub claim. Carrying it on the event is what
        // lets the consumer revoke without a database round trip on the path that has the tightest
        // deadline in the system (FR-003, five minutes).
        Assert.Equal(employee.ExternalSubject, raised.ExternalSubject);
        Assert.Equal(employee.Id, raised.EmployeeId);
        Assert.Equal("chat.employee.deactivated.v1", raised.EventType);
    }

    // -------------------------------------------------------------------------------------------
    // Directory attribute sync must not be able to grant or revoke access
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Updating_directory_attributes_never_changes_status()
    {
        Employee employee = ActiveEmployee();
        employee.Deactivate(FixedClock());

        employee.UpdateDirectoryAttributes(
            displayName: "Nguyễn Thị Vân An (Contractor)",
            email: "an.nguyen@internalchat.local",
            avatarUrl: null,
            FixedClock("2026-08-03T09:00:00Z"));

        // A rename must not silently restore access. If attribute sync could flip status, a
        // display-name change and a revocation would be the same operation — and only one of them
        // has a five-minute budget attached.
        Assert.Equal(EmployeeStatus.Deactivated, employee.Status);
        Assert.NotNull(employee.DeactivatedAt);
    }

    // -------------------------------------------------------------------------------------------
    // Required directory attributes
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("", "Display Name", "a@b.local")]
    [InlineData("   ", "Display Name", "a@b.local")]
    [InlineData("subject", "", "a@b.local")]
    [InlineData("subject", "Display Name", "")]
    public void Projection_requires_subject_display_name_and_email(
        string subject,
        string displayName,
        string email)
    {
        // An employee with no external subject can never be matched to a token, so the row would
        // be permanently unreachable rather than merely incomplete.
        Assert.Throws<ArgumentException>(() =>
            Employee.Project(Guid.CreateVersion7(), subject, displayName, email, FixedClock()));
    }

    [Fact]
    public void Projection_trims_surrounding_whitespace()
    {
        Employee employee = Employee.Project(
            Guid.CreateVersion7(),
            externalSubject: "  dev-an.nguyen  ",
            displayName: "  Nguyễn Thị Vân An  ",
            email: "  an.nguyen@internalchat.local  ",
            FixedClock());

        // The subject is compared against a token claim byte for byte. A trailing space from a
        // directory export would make every sign-in fail to match, with nothing in the logs
        // suggesting why.
        Assert.Equal("dev-an.nguyen", employee.ExternalSubject);
        Assert.Equal("an.nguyen@internalchat.local", employee.Email);
    }
}
