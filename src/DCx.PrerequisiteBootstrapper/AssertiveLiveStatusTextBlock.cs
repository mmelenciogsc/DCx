using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace DCx.PrerequisiteBootstrapper;

/// <summary>
/// A status surface whose peer explicitly reports an assertive live setting. WPF's plain
/// TextBlock peer does not expose this UIA property on all supported providers.
/// </summary>
public sealed class AssertiveLiveStatusTextBlock : TextBlock
{
    protected override AutomationPeer OnCreateAutomationPeer() => new AssertiveLiveStatusTextBlockAutomationPeer(this);
}

/// <summary>Provides the cross-process UIA contract consumed by Narrator, JAWS, NVDA, and UIA clients.</summary>
internal sealed class AssertiveLiveStatusTextBlockAutomationPeer : TextBlockAutomationPeer
{
    public AssertiveLiveStatusTextBlockAutomationPeer(AssertiveLiveStatusTextBlock owner)
        : base(owner)
    {
    }

    protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Assertive;
}
