#if NET6_0
public static class Int32
{
  public static int Min(int a, int b) => a > b ? b : a;
}
#endif
