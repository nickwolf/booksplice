using BookSplice.Core.Analysis;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.EditCopy;
using BookSplice.FFmpeg.Validation;

namespace BookSplice.FFmpeg.Tests.EditCopy;

public sealed class EditCopyChecksTests
{
  [Fact]
  public void AFailedAudioCodecCheckDoesNotBlockAnEdit()
  {
    var report = Report(new ExistingFileCheck(ValidationCodes.AudioCodec, ExistingCheckStatus.Failed, "The primary audio stream is not AAC-LC."));

    var checks = EditCopyService.EditChecks(report);

    Assert.False(report.IsValid);
    var codec = Assert.Single(checks, check => check.Code == ValidationCodes.AudioCodec);
    Assert.False(codec.Passed);
    Assert.False(codec.Required);
    Assert.DoesNotContain(checks, check => check.Required && !check.Passed);
  }

  [Fact]
  public void AnyOtherFailedRequiredCheckStillBlocksAnEdit()
  {
    var report = Report(
      new ExistingFileCheck(ValidationCodes.AudioCodec, ExistingCheckStatus.Failed, "The primary audio stream is not AAC-LC."),
      new ExistingFileCheck(ExistingM4bChecker.ChaptersWithinAudio, ExistingCheckStatus.Failed, "The last chapter ends after the audio."));

    var checks = EditCopyService.EditChecks(report);

    Assert.True(Assert.Single(checks, check => check.Code == ExistingM4bChecker.ChaptersWithinAudio).Required);
    Assert.Contains(checks, check => check.Required && !check.Passed);
  }

  [Fact]
  public void ChecksThatDidNotRunAreLeftOut()
  {
    var report = Report(
      new ExistingFileCheck(ValidationCodes.FullDecode, ExistingCheckStatus.NotChecked, "Decode all audio was not selected.", false),
      new ExistingFileCheck(ExistingM4bChecker.CoverFound, ExistingCheckStatus.Note, "No cover.", false));

    Assert.DoesNotContain(EditCopyService.EditChecks(report), check => check.Code is ValidationCodes.FullDecode or ExistingM4bChecker.CoverFound);
  }

  private static ExistingFileCheckReport Report(params ExistingFileCheck[] extra)
    => new(@"C:\Books\Original.m4b", [new(ValidationCodes.FileExists, ExistingCheckStatus.Passed, "Passed."), new(ValidationCodes.ContainerParse, ExistingCheckStatus.Passed, "Passed."), .. extra], []);
}
