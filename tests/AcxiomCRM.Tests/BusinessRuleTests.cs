using System.ComponentModel.DataAnnotations;
using AcxiomCRM.Dtos;
using AcxiomCRM.Models;
using AcxiomCRM.Services;

namespace AcxiomCRM.Tests;

public class BusinessRuleTests
{
    private static List<ValidationResult> Validate(object model)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, new ValidationContext(model), results, validateAllProperties: true);
        return results;
    }

    private static OpportunityInputDto ValidOpportunity() => new()
    {
        OpportunityName = "Test deal",
        CustomerId = 1,
        Amount = 1000,
        Probability = 50,
        ExpectedCloseDate = DateTime.Today.AddDays(10),
        Stage = OpportunityStage.Proposal
    };

    [Fact]
    public void Opportunity_amount_zero_is_rejected_for_active_stage()
    {
        var errors = OpportunityRules.Validate(0, 50, DateTime.Today, OpportunityStage.Proposal).ToList();
        Assert.Contains(errors, e => e.Message == "Opportunity Amount must be greater than 0.");
    }

    [Fact]
    public void Opportunity_negative_amount_is_rejected_even_when_closed()
    {
        var errors = OpportunityRules.Validate(-1, 0, DateTime.Today, OpportunityStage.Lost).ToList();
        Assert.Contains(errors, e => e.Field == "Amount");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Probability_outside_0_to_100_is_rejected(int probability)
    {
        var errors = OpportunityRules.Validate(1000, probability, DateTime.Today, OpportunityStage.Proposal).ToList();
        Assert.Contains(errors, e => e.Message == "Probability must be between 0 and 100.");
    }

    [Fact]
    public void Expected_close_date_in_past_is_rejected_only_for_active_opportunities()
    {
        var yesterday = DateTime.Today.AddDays(-1);
        Assert.Contains(OpportunityRules.Validate(1000, 50, yesterday, OpportunityStage.Negotiation),
            e => e.Message == "Expected Close Date cannot be in the past.");
        Assert.Empty(OpportunityRules.Validate(1000, 100, yesterday, OpportunityStage.Won));
    }

    [Fact]
    public void Weighted_pipeline_is_amount_times_probability_over_100()
    {
        Assert.Equal(250m, OpportunityRules.Weighted(1000m, 25));
    }

    [Fact]
    public void Opportunity_dto_annotations_mirror_business_rules()
    {
        var dto = ValidOpportunity();
        Assert.Empty(Validate(dto));

        dto.Amount = 0;
        Assert.Contains(Validate(dto), r => r.ErrorMessage == "Opportunity Amount must be greater than 0.");

        dto.Stage = OpportunityStage.Lost;
        Assert.DoesNotContain(Validate(dto), r => r.MemberNames.Contains("Amount"));
    }

    [Fact]
    public void Follow_up_date_before_today_is_rejected()
    {
        var dto = new FollowUpInputDto { Subject = "Call", FollowUpDate = DateTime.Today.AddDays(-1), FollowUpType = FollowUpType.Call, CustomerId = 1 };
        Assert.Contains(Validate(dto), r => r.ErrorMessage == "Follow-up date cannot be earlier than today.");

        dto.FollowUpDate = DateTime.Today.AddHours(9);
        Assert.Empty(Validate(dto));
    }

    [Theory]
    [InlineData("not-an-email", "9876543210", "Enter a valid email address.")]
    [InlineData("a@b.com", "12345", "Enter a valid phone number.")]
    [InlineData("a@b.com", "5876543210", "Enter a valid phone number.")]
    public void Customer_email_and_phone_formats_are_validated(string email, string phone, string message)
    {
        var dto = new CustomerInputDto { CustomerName = "Test", Email = email, Phone = phone, Status = CustomerStatus.Active };
        Assert.Contains(Validate(dto), r => r.ErrorMessage == message);
    }

    [Fact]
    public void Customer_name_is_required_and_length_limited()
    {
        var dto = new CustomerInputDto { CustomerName = "", Email = "a@b.com", Phone = "9876543210", Status = CustomerStatus.Active };
        Assert.Contains(Validate(dto), r => r.ErrorMessage == "Customer Name is required.");

        dto.CustomerName = new string('x', 101);
        Assert.Contains(Validate(dto), r => r.MemberNames.Contains(nameof(CustomerInputDto.CustomerName)));
    }

    [Fact]
    public void Lead_status_must_be_a_defined_value()
    {
        var dto = new LeadInputDto { LeadName = "Lead", Source = LeadSource.Website, Status = (LeadStatus)99, Priority = Priority.Low, ExpectedValue = 10 };
        Assert.Contains(Validate(dto), r => r.ErrorMessage == "Select a valid lead status.");
    }

    [Theory]
    [InlineData(LeadStatus.New, LeadStatus.Contacted, true)]
    [InlineData(LeadStatus.Contacted, LeadStatus.Qualified, true)]
    [InlineData(LeadStatus.New, LeadStatus.Converted, false)]
    [InlineData(LeadStatus.Converted, LeadStatus.New, false)]
    [InlineData(LeadStatus.Lost, LeadStatus.Qualified, false)]
    public void Lead_workflow_prevents_invalid_transitions(LeadStatus from, LeadStatus to, bool allowed)
    {
        Assert.Equal(allowed, LeadWorkflow.CanTransition(from, to));
    }
}
