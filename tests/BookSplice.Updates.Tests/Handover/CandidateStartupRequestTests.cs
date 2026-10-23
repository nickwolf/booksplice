using BookSplice.Updates.Handover;
using BookSplice.Updates.Windows;

namespace BookSplice.Updates.Tests.Handover;

public sealed class CandidateStartupRequestTests
{
  [Fact]
  public void SharedWireRoundTripsAndGuiPackagePredicateStillRejectsMismatch()
  {
    var identity = new FileIdentity(1, "0123456789ABCDEF0123456789ABCDEF");
    var request = new CandidateStartupRequest(UpdatePipe.NewPipeName(),
      Guid.NewGuid(), Convert.ToBase64String(UpdatePipe.NewNonce()),
      new ProcessIdentity(42, 123, @"C:\BookSplice\BookSplice.Launcher.exe"),
      identity, new string('a', 64), @"C:\Data\BookSplice", identity,
      "absent", "2.0.0");
    var encoded = request.Encode();
    Assert.Equal(request, CandidateStartupRequest.Parse(encoded, version => version == "2.0.0"));
    Assert.Throws<InvalidDataException>(() =>
      CandidateStartupRequest.Parse(encoded, _ => false));
  }
}
