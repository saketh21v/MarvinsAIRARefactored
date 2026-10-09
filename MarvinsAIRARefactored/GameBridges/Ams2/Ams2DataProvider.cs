namespace MarvinsAIRARefactored.GameBridges.Ams2;

public abstract class Ams2DataProvider
{
	public abstract bool TryOpen();

	public abstract void Close();

	public abstract bool TryReadBuffer( byte[] destination );
}
