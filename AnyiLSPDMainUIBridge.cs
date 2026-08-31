namespace AnyiLSPD
{
    public static class AnyiLSPDMainUIBridge
    {
        public static string Status()
        {
            return AnyiLSPDCore.Instance == null ? "Police Authority core unavailable." : AnyiLSPDCore.Instance.StatusLine;
        }

        public static void SelectRole(LspdResponseRole role)
        {
            if (AnyiLSPDCore.Instance == null) return;
            AnyiLSPDCore.Instance.UpdateRole(role);
        }
    }
}
