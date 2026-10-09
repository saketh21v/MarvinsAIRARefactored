using System.IO;
using System.IO.MemoryMappedFiles;

namespace MarvinsAIRARefactored.GameBridges.Ams2;

/// <summary>
/// Reads Automobilista 2's built-in "$pcars2$" shared memory map. The game only creates it when
/// Options &gt; System &gt; Shared Memory is set to Project CARS 2, so a missing map is a setup problem
/// rather than a missing plugin.
/// </summary>
public class Ams2LiveDataProvider : Ams2DataProvider
{
	private MemoryMappedFile? _memoryMappedFile = null;
	private MemoryMappedViewAccessor? _viewAccessor = null;

	public override bool TryOpen()
	{
		if ( _viewAccessor == null )
		{
			try
			{
				_memoryMappedFile = MemoryMappedFile.OpenExisting( Ams2Constants.SharedMemoryMapName, MemoryMappedFileRights.Read );

				_viewAccessor = _memoryMappedFile.CreateViewAccessor( 0, 0, MemoryMappedFileAccess.Read );
			}
			catch ( FileNotFoundException )
			{
			}
		}

		return ( _viewAccessor != null ) && ( _viewAccessor.Capacity >= Ams2Constants.SharedMemorySize );
	}

	public override void Close()
	{
		_viewAccessor?.Dispose();
		_memoryMappedFile?.Dispose();

		_viewAccessor = null;
		_memoryMappedFile = null;
	}

	public override bool TryReadBuffer( byte[] destination )
	{
		if ( ( _viewAccessor == null ) || ( _viewAccessor.Capacity < destination.Length ) )
		{
			return false;
		}

		_viewAccessor.ReadArray( 0, destination, 0, destination.Length );

		return true;
	}
}
