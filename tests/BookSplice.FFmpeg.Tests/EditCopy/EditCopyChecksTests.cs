using BookSplice.Core.Analysis;
using BookSplice.Core.Validation;
using BookSplice.FFmpeg.Commands;
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

  [Fact]
  public void UserDataMatchesWhenOnlyTheChapterAndTagBoxesDiffer()
  {
    var original = new Mp4UserData([Raw("meta", 1), Raw("chpl", 2), Raw("Xtra", 3)], Raw("meta", 4).Raw);
    var copy = new Mp4UserData([Raw("meta", 5), Raw("chpl", 6), Raw("Xtra", 3)], Raw("meta", 4).Raw);

    Assert.True(EditCopyService.SameUserData(copy, original));
  }

  [Fact]
  public void UserDataDiffersWhenABoxIsMissingFromTheCopy()
  {
    var original = new Mp4UserData([Raw("meta", 1), Raw("Xtra", 3), Raw("cprt", 7)], Raw("meta", 4).Raw);

    Assert.False(EditCopyService.SameUserData(new Mp4UserData([Raw("meta", 1), Raw("Xtra", 3)], original.MoovMeta), original));
    Assert.False(EditCopyService.SameUserData(new Mp4UserData(original.UdtaChildren, null), original));
  }

  [Fact]
  public void UserDataDiffersWhenABoxChangesOrMoves()
  {
    var original = new Mp4UserData([Raw("Xtra", 3), Raw("cprt", 7)], null);

    Assert.False(EditCopyService.SameUserData(new Mp4UserData([Raw("Xtra", 8), Raw("cprt", 7)], null), original));
    Assert.False(EditCopyService.SameUserData(new Mp4UserData([Raw("cprt", 7), Raw("Xtra", 3)], null), original));
  }

  private static Mp4RawBox Raw(string type, byte value) => new(type, [0, 0, 0, 9, .. System.Text.Encoding.Latin1.GetBytes(type), value]);

  private static ExistingFileCheckReport Report(params ExistingFileCheck[] extra)
    => new(@"C:\Books\Original.m4b", [new(ValidationCodes.FileExists, ExistingCheckStatus.Passed, "Passed."), new(ValidationCodes.ContainerParse, ExistingCheckStatus.Passed, "Passed."), .. extra], []);
}
