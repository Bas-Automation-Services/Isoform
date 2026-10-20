using Isoform.Modeling;

namespace Isoform.Tests;

public class FrepEditSuiteTests : EditSuiteTests
{
    protected override IModeler CreateModeler() => new FrepModeler(new ModelingSettings());
}
