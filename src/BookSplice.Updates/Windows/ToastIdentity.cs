namespace BookSplice.Updates.Windows;

// Test builds get their own AppUserModelId and activator so they never touch the Release registry keys.
public sealed record ToastIdentity(string AppUserModelId, Guid ActivatorClsid)
{
  public static ToastIdentity Release { get; } =
    new("BookSplice.App", new Guid("129caa63-8b2d-4578-a9e0-82cc04123257"));

  public static ToastIdentity Test { get; } =
    new("BookSplice.Test.App", new Guid("c9cd0ba0-726e-48ef-b23f-4e0a15a106f8"));

  public static ToastIdentity For(BuildFlavor flavor) => flavor == BuildFlavor.Release ? Release : Test;

  public static ToastIdentity Current => For(UpdateBuild.Flavor);
}
