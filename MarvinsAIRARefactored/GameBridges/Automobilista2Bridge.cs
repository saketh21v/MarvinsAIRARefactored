using System.Runtime.InteropServices;
using System.Text;

using IRSDKSharper;

using MarvinsAIRARefactored.GameBridges.Ams2;

namespace MarvinsAIRARefactored.GameBridges;

/// <summary>
/// Automobilista 2 bridge. AMS2 writes the Project CARS 2 shared memory map ("$pcars2$") when
/// Options &gt; System &gt; Shared Memory is set to Project CARS 2 - see Ams2Data.cs. Nothing has to be
/// installed. The map is updated once per graphics frame (up to about 200 Hz), and it has suspension
/// velocity and a per-tyre terrain material, but it has no steering-shaft torque: mEngineTorque is the
/// crankshaft, not the wheel. Torque samples are therefore written as zero, and the bridge does not claim
/// SteeringWheelTorque360Hz. Effect modules that synthesize a force from wheel angle, speed, lateral
/// acceleration, ABS or suspension still have data; a graph that only reshapes the game's torque does not.
/// The steering value is a normalized -1..1 across the rotation configured in the game, which the map does
/// not publish, so the wheel angle is scaled by the Accessibility wheelbase rotation (the same match the
/// other bridges ask the user to make). Axis signs below are the SMS local frame used by motion-rig
/// readers of this map (+X right, +Y up, +Z forward) and are not capture-validated.
/// </summary>
public class Automobilista2Bridge : GameBridgeAdapter
{
	public override string GameName => "Automobilista 2";
	public override string LocalizationKey => "Automobilista2";
	public override string[] ProcessNames => [ "AMS2AVX", "AMS2" ];
	public override GameBridgeCapabilities Capabilities => GameBridgeCapabilities.ShockVelocities | GameBridgeCapabilities.TireRumble;

	public override bool IsImplemented => true;

	// positive game steering is taken to be a right turn (the device axis). iRacing's angle is left-positive.
	private const float SteeringAngleSign = -1f;

	// local +X is right, so lateral (left-positive in iRacing) flips. Yaw about the up axis is taken as
	// right-positive in this left-handed frame, so it flips to iRacing's counterclockwise-positive yaw.
	private const float LateralSign = -1f;
	private const float YawSign = -1f;
	private const float YawRateSign = -1f;

	// mSuspensionVelocity is "rate of change of pushrod deflection". Travel is in meters, so this is taken
	// as m/s. The sign relative to iRacing's shock velocity is unvalidated.
	private const float ShockVelocitySign = 1f;

	// consumers of Tire*RumblePitch (the belt tensioner) only test that the value is above zero. The map
	// says which tyre is on a rumble strip and nothing about a pitch, so a tyre on a strip reports 1 Hz.
	private const float RumblePitchOnStrip = 1f;

	private const int SamplesPerFrame = GameBridgeVarTable.SamplesPerFrame360Hz;
	private const int SubSampleFrequency = 360;

	private const float MinimumWheelbaseRangeDegrees = 60f;
	private const float FallbackWheelbaseRangeDegrees = 900f;

	private GameBridgeVarTable? _varTable = null;
	private Ams2DataProvider? _provider = null;

	// the bridge is pumped from the playout timer worker thread (see Pump); this lock keeps a Stop from
	// a background task from closing the provider while a pump is mid-read
	private readonly object _pumpLock = new();
	private bool _providerOpen = false;
	private bool _missingMapLogged = false;
	private double _lastOpenAttemptSeconds = double.MinValue;
	private double _nextSubSampleSeconds = 0.0;

	// if the game ever recreates its shared memory map, a reader holding the old section keeps reading its
	// orphaned frozen copy forever - same failure the LMU bridge hit across session loads - so the pump
	// recycles the map while the physics output is frozen (see the stale-map recycle in Pump)
	private const double StaleMapRecycleSeconds = 2.0;

	private double _lastMapRecycleSeconds = double.MinValue;
	private bool _recyclingStaleMap = false;

	private byte[] _sharedBuffer = [];

	private long _frameCounter = 0;

	private bool _hasShared = false;
	private bool _versionLogged = false;
	private Ams2Shared _shared;
	private uint _previousSequence = 0;

	private double _sessionTime = 0.0;
	private bool _sessionClockPrimed = false;
	private double _previousClockPumpSeconds = 0.0;
	private int _trackHash = 0;
	private int _carHash = 0;
	private uint _sessionStateSeen = uint.MaxValue;

	private float _previousFuelLiters = 0f;
	private float _fuelUsePerHour = 0f;
	private float _frameRate = 0f;

	private int _subSampleIndex = 0;
	private readonly float[] _torqueSamples = new float[ SamplesPerFrame ];
	private readonly float[,] _shockSamples = new float[ 4, SamplesPerFrame ];
	private readonly float[] _lastShockVelocities = new float[ 4 ];

	private double _steeringScale = 0.0;
	private int _steeringScaleSampleCount = 0;

	private string _lastSessionInfoSignature = string.Empty;
	private double _lastSessionInfoUpdateTime = double.MinValue;

	private readonly float[] _carIdxFloatScratch = new float[ GameBridgeVarTable.MaxNumCars ];
	private readonly int[] _carIdxIntScratch = new int[ GameBridgeVarTable.MaxNumCars ];

	public override void Start()
	{
		var app = App.Instance!;

		app.Logger.WriteLine( "[Automobilista2Bridge] Start >>>" );

		var layoutError = Ams2Layout.Check();

		if ( layoutError != null )
		{
			throw new InvalidOperationException( $"AMS2 shared memory layout check failed: {layoutError}" );
		}

		_varTable = new GameBridgeVarTable();

		DataSource = _varTable.DataSource;

		_provider = CreateProvider();

		_sharedBuffer = new byte[ Ams2Constants.SharedMemorySize ];

		ResetState();

		app.Logger.WriteLine( "[Automobilista2Bridge] <<< Start" );
	}

	public override void Stop()
	{
		var app = App.Instance!;

		app.Logger.WriteLine( "[Automobilista2Bridge] Stop >>>" );

		lock ( _pumpLock )
		{
			_provider?.Close();
			_provider = null;

			_providerOpen = false;
		}

		app.Logger.WriteLine( "[Automobilista2Bridge] <<< Stop" );
	}

	protected virtual Ams2DataProvider CreateProvider()
	{
		return new Ams2LiveDataProvider();
	}

	private void ResetState()
	{
		_providerOpen = false;
		_missingMapLogged = false;
		_lastOpenAttemptSeconds = double.MinValue;
		_nextSubSampleSeconds = 0.0;

		_lastMapRecycleSeconds = double.MinValue;
		_recyclingStaleMap = false;

		_frameCounter = 0;

		_hasShared = false;
		_versionLogged = false;

		_previousSequence = 0;

		_sessionTime = 0.0;
		_sessionClockPrimed = false;
		_previousClockPumpSeconds = 0.0;
		_trackHash = 0;
		_carHash = 0;
		_sessionStateSeen = uint.MaxValue;

		_previousFuelLiters = 0f;
		_fuelUsePerHour = 0f;
		_frameRate = 0f;

		LastDataActivitySeconds = double.MinValue;

		_subSampleIndex = 0;
		Array.Clear( _torqueSamples );
		Array.Clear( _shockSamples );
		Array.Clear( _lastShockVelocities );

		_steeringScale = 0.0;
		_steeringScaleSampleCount = 0;

		_lastSessionInfoSignature = string.Empty;
		_lastSessionInfoUpdateTime = double.MinValue;
	}

	#region pump

	// Called from the playout timer worker thread (~360 Hz, kernel-scheduled) immediately before the
	// racing wheel update. The 360 Hz sub-sample schedule is kept internally; zero, one, or occasionally two
	// sub-samples are taken per timer tick, each stamped with its scheduled time.
	public override void Pump( double totalSeconds )
	{
		lock ( _pumpLock )
		{
			if ( _provider == null )
			{
				return;
			}

			if ( !_providerOpen )
			{
				if ( totalSeconds - _lastOpenAttemptSeconds < 1.0 )
				{
					return;
				}

				_lastOpenAttemptSeconds = totalSeconds;

				if ( !_provider.TryOpen() )
				{
					if ( !_missingMapLogged )
					{
						_missingMapLogged = true;

						App.Instance!.Logger.WriteLine( "[Automobilista2Bridge] Shared memory \"$pcars2$\" was not found. In Automobilista 2 set Options > System > Shared Memory to Project CARS 2." );
					}

					return;
				}

				_providerOpen = true;
				_missingMapLogged = false;

				_nextSubSampleSeconds = totalSeconds;

				// during a stale-map recycle the map is reopened every couple of seconds until the physics
				// output resumes, so the recycle logs its own start/resume lines instead of spamming this one
				if ( !_recyclingStaleMap )
				{
					App.Instance!.Logger.WriteLine( "[Automobilista2Bridge] Shared memory opened - pumping" );
				}
			}

			// if the game ever recreates its shared memory map, holding the handle to the old section means
			// reading its orphaned frozen copy forever - so while the physics output has been frozen for a
			// while, periodically drop and reopen the map to reattach to the live section (reopening the same
			// still-valid map while the game is merely paused or in a menu is cheap and harmless)
			if ( ( LastDataActivitySeconds != double.MinValue ) && ( totalSeconds - LastDataActivitySeconds >= StaleMapRecycleSeconds ) && ( totalSeconds - _lastMapRecycleSeconds >= StaleMapRecycleSeconds ) )
			{
				if ( !_recyclingStaleMap )
				{
					_recyclingStaleMap = true;

					App.Instance!.Logger.WriteLine( "[Automobilista2Bridge] Telemetry stopped advancing - recycling the shared memory map until it resumes" );
				}

				_lastMapRecycleSeconds = totalSeconds;

				_provider.Close();

				_providerOpen = false;

				return;
			}

			// if the timer stalled for a while, resynchronize instead of bursting a backlog of sub-samples
			if ( totalSeconds - _nextSubSampleSeconds > 0.25 )
			{
				_nextSubSampleSeconds = totalSeconds;
			}

			while ( _nextSubSampleSeconds <= totalSeconds )
			{
				ProcessSubSample( _nextSubSampleSeconds );

				_nextSubSampleSeconds += 1.0 / SubSampleFrequency;
			}
		}
	}

	// Runs at 360 Hz. Every tick refreshes the shared struct (UpdateShared re-parses only when the map's
	// sequence number advanced, holding its values otherwise) and captures the current shock velocity into
	// the 360 Hz slot; once six slots are filled (60 Hz) a full frame is committed. There is no torque to
	// capture - the samples stay at the zeros ResetState wrote.
	private void ProcessSubSample( double pumpSeconds )
	{
		if ( _provider?.TryReadBuffer( _sharedBuffer ) ?? false )
		{
			UpdateShared( pumpSeconds );
		}

		for ( var wheelIndex = 0; wheelIndex < 4; wheelIndex++ )
		{
			_shockSamples[ wheelIndex, _subSampleIndex ] = _lastShockVelocities[ wheelIndex ];
		}

		if ( _subSampleIndex == SamplesPerFrame - 1 )
		{
			_frameCounter++;

			if ( _hasShared )
			{
				UpdateSessionInfo( pumpSeconds );

				WriteTelemetryFrame();

				_varTable!.DataSource.CommitFrame();
			}
		}

		_subSampleIndex = ( _subSampleIndex + 1 ) % SamplesPerFrame;
	}

	#endregion

	#region shared map parsing

	private void UpdateShared( double pumpSeconds )
	{
		// the game increments the sequence at the start and end of a write, so an odd value means a write
		// is in progress. Skip it and hold the previous sample rather than parsing a torn struct.
		var sequence = BitConverter.ToUInt32( _sharedBuffer, Ams2Constants.SequenceNumberOffset );

		if ( ( sequence & 1 ) != 0 )
		{
			return;
		}

		var shared = ReadStruct<Ams2Shared>( _sharedBuffer, 0 );

		if ( BitConverter.ToUInt32( _sharedBuffer, Ams2Constants.SequenceNumberOffset ) != sequence )
		{
			return;
		}

		// version 8 is the first layout that carries suspension velocity. Older maps are a different size
		// and are rejected by the provider's length check; a short version inside a full-size map is ignored.
		if ( shared.mVersion < 8 )
		{
			return;
		}

		if ( !_versionLogged )
		{
			_versionLogged = true;

			App.Instance!.Logger.WriteLine( $"[Automobilista2Bridge] Shared memory version {shared.mVersion}, build {shared.mBuildVersionNumber}" );
		}

		if ( _hasShared && ( sequence == _previousSequence ) )
		{
			return;
		}

		var advancing = IsSimAdvancing( shared.mGameState );

		if ( advancing )
		{
			LastDataActivitySeconds = pumpSeconds;

			if ( _recyclingStaleMap )
			{
				_recyclingStaleMap = false;

				App.Instance!.Logger.WriteLine( "[Automobilista2Bridge] Telemetry resumed - reattached to the live shared memory map" );
			}
		}

		var trackHash = HashName( shared.mTrackLocation ) ^ HashName( shared.mTrackVariation );
		var carHash = HashName( shared.mCarName );

		if ( _hasShared && ( ( trackHash != _trackHash ) || ( carHash != _carHash ) || ( shared.mSessionState != _sessionStateSeen ) ) )
		{
			_sessionTime = 0.0;
			_sessionClockPrimed = false;
			_previousFuelLiters = FuelLiters( shared );
			_fuelUsePerHour = 0f;
		}

		_trackHash = trackHash;
		_carHash = carHash;
		_sessionStateSeen = shared.mSessionState;

		if ( advancing )
		{
			if ( _sessionClockPrimed )
			{
				var deltaSeconds = pumpSeconds - _previousClockPumpSeconds;

				if ( ( deltaSeconds > 0.0 ) && ( deltaSeconds < 1.0 ) )
				{
					_sessionTime += deltaSeconds;

					var liters = FuelLiters( shared );
					var fuelPerSecond = ( _previousFuelLiters - liters ) / deltaSeconds;

					if ( fuelPerSecond >= 0.0 )
					{
						_fuelUsePerHour += 0.1f * ( (float) ( fuelPerSecond * 3600.0 ) - _fuelUsePerHour );
					}

					_previousFuelLiters = liters;

					var frameCount = ( sequence - _previousSequence ) / 2.0;

					if ( ( frameCount > 0.0 ) && ( frameCount < 100.0 ) )
					{
						_frameRate += 0.1f * ( (float) ( frameCount / deltaSeconds ) - _frameRate );
					}
				}
			}
			else
			{
				_sessionClockPrimed = true;
				_previousFuelLiters = FuelLiters( shared );
			}

			_previousClockPumpSeconds = pumpSeconds;
		}

		// a timed session counts mEventTimeRemaining down in milliseconds from mSessionDuration minutes.
		// that game clock replaces the locally integrated one whenever both ends of it are published.
		if ( ( shared.mEventTimeRemaining >= 0f ) && ( shared.mSessionDuration > 0f ) )
		{
			var fromCountdown = ( shared.mSessionDuration * 60.0 ) - ( shared.mEventTimeRemaining / 1000.0 );

			if ( fromCountdown >= 0.0 )
			{
				_sessionTime = fromCountdown;
			}
		}

		for ( var wheelIndex = 0; wheelIndex < 4; wheelIndex++ )
		{
			_lastShockVelocities[ wheelIndex ] = ShockVelocitySign * shared.mSuspensionVelocity[ wheelIndex ];
		}

		_previousSequence = sequence;
		_shared = shared;
		_hasShared = true;
	}

	private static bool IsSimAdvancing( uint gameState )
	{
		return ( gameState == (uint) Ams2GameState.IngamePlaying ) || ( gameState == (uint) Ams2GameState.IngameInmenuTimeTicking ) || ( gameState == (uint) Ams2GameState.IngameReplay );
	}

	private static T ReadStruct<T>( byte[] buffer, int offset ) where T : struct
	{
		return MemoryMarshal.Read<T>( buffer.AsSpan( offset ) );
	}

	private static string ReadString( ReadOnlySpan<byte> bytes )
	{
		var length = bytes.IndexOf( (byte) 0 );

		if ( length == -1 )
		{
			length = bytes.Length;
		}

		return Encoding.UTF8.GetString( bytes[ ..length ] );
	}

	private static int HashName( ReadOnlySpan<byte> bytes )
	{
		var length = bytes.IndexOf( (byte) 0 );

		if ( length == -1 )
		{
			length = bytes.Length;
		}

		var hash = 0;

		for ( var i = 0; i < length; i++ )
		{
			hash = unchecked( ( hash * 31 ) + bytes[ i ] );
		}

		return hash & 0x7fffffff;
	}

	// mFuelLevel is a 0..1 fraction. mFuelCapacity is liters (the header's "0..1" range on it is the
	// level's range copied onto the wrong field).
	private static float FuelLiters( in Ams2Shared shared )
	{
		if ( shared.mFuelCapacity <= 0f )
		{
			return 0f;
		}

		return Math.Clamp( shared.mFuelLevel, 0f, 1f ) * shared.mFuelCapacity;
	}

	#endregion

	#region steering wheel angle

	private const double SteeringScaleAlpha = 0.05;
	private const int SteeringScaleMinSamples = 100;
	private const double SteeringScaleMinMagnitude = 1.0;
	private const double SteeringScaleMaxMagnitude = 16.0;

	private float ComputeSteeringWheelAngle( double gameSteering, double steeringRangeRadians )
	{
		var gameAngle = SteeringAngleSign * gameSteering * steeringRangeRadians * 0.5;

		// the game clamps its steering value at full lock, so a soft lock spring driven by the game's angle
		// can never see the wheel go past the stop - iRacing reports the PHYSICAL wheel angle instead, which
		// we reconstruct here from the DirectInput axis, calibrated against the game's angle while the
		// steering is inside its linear (unclamped) region. The map does not publish the rotation that
		// ±1 spans, so gameAngle is scaled by the Accessibility wheelbase range; that calibration is exact
		// when the game's rotation matches the wheelbase, which is the setup the other bridges already require.
		var physicalPosition = App.Instance!.Accessibility.GameSteeringPosition;

		if ( ( Math.Abs( gameSteering ) < 0.95 ) && ( Math.Abs( physicalPosition ) > 0.10 ) )
		{
			var instantScale = gameAngle / physicalPosition;

			if ( ( Math.Abs( instantScale ) >= SteeringScaleMinMagnitude ) && ( Math.Abs( instantScale ) <= SteeringScaleMaxMagnitude ) )
			{
				_steeringScale += SteeringScaleAlpha * ( instantScale - _steeringScale );

				_steeringScaleSampleCount++;
			}
		}

		if ( _steeringScaleSampleCount >= SteeringScaleMinSamples )
		{
			return (float) ( _steeringScale * physicalPosition );
		}

		return (float) gameAngle;
	}

	private static float GetSteeringRangeRadians()
	{
		var rangeDegrees = DataContext.DataContext.Instance.Settings.AccessibilityWheelbaseRange;

		if ( rangeDegrees < MinimumWheelbaseRangeDegrees )
		{
			rangeDegrees = FallbackWheelbaseRangeDegrees;
		}

		return rangeDegrees * (float) ( Math.PI / 180.0 );
	}

	#endregion

	#region telemetry frame mapping

	private void WriteTelemetryFrame()
	{
		var varTable = _varTable!;
		var dataSource = varTable.DataSource;

		var shared = _shared;
		var playerIndex = PlayerIndex( shared );
		var player = ( playerIndex >= 0 ) ? shared.mParticipantInfo[ playerIndex ] : default;
		var pitMode = (Ams2PitMode) shared.mPitMode;
		var onTrack = IsOnTrack( shared, playerIndex, pitMode );

		// pedals and gear. clutch follows the throttle/brake pedal convention (1 = pressed = disengaged);
		// iRacing's clutch is 1 when engaged, so it is inverted. That reading of mClutch is assumed.

		dataSource.SetFloat( varTable.Throttle, Math.Clamp( shared.mThrottle, 0f, 1f ) );
		dataSource.SetFloat( varTable.ThrottleRaw, Math.Clamp( shared.mUnfilteredThrottle, 0f, 1f ) );
		dataSource.SetFloat( varTable.Brake, Math.Clamp( shared.mBrake, 0f, 1f ) );
		dataSource.SetFloat( varTable.BrakeRaw, Math.Clamp( shared.mUnfilteredBrake, 0f, 1f ) );
		dataSource.SetFloat( varTable.Clutch, 1f - Math.Clamp( shared.mClutch, 0f, 1f ) );
		dataSource.SetBool( varTable.BrakeABSactive, shared.mAntiLockActive != 0 );
		dataSource.SetInt( varTable.Gear, shared.mGear );
		dataSource.SetFloat( varTable.RPM, Math.Max( 0f, shared.mRpm ) );

		// motion. header lists acceleration in "metres per-second", which is the velocity unit copied onto
		// the wrong fields; the values are the m/s^2 readings motion rigs consume. vertical is taken to
		// already include gravity (iRacing's VertAccel does too), so it is not offset.

		var velocityX = shared.mLocalVelocity[ Ams2Constants.VecZ ];
		var velocityY = LateralSign * shared.mLocalVelocity[ Ams2Constants.VecX ];

		dataSource.SetFloat( varTable.Speed, Math.Max( 0f, shared.mSpeed ) );
		dataSource.SetFloat( varTable.VelocityX, velocityX );
		dataSource.SetFloat( varTable.VelocityY, velocityY );
		dataSource.SetFloat( varTable.LongAccel, shared.mLocalAcceleration[ Ams2Constants.VecZ ] );
		dataSource.SetFloat( varTable.LatAccel, LateralSign * shared.mLocalAcceleration[ Ams2Constants.VecX ] );
		dataSource.SetFloat( varTable.VertAccel, shared.mLocalAcceleration[ Ams2Constants.VecY ] );
		dataSource.SetFloat( varTable.YawRate, YawRateSign * shared.mAngularVelocity[ Ams2Constants.VecY ] );

		var yaw = YawSign * shared.mOrientation[ Ams2Constants.VecY ];

		dataSource.SetFloat( varTable.Yaw, yaw );
		dataSource.SetFloat( varTable.YawNorth, yaw );
		dataSource.SetFloat( varTable.Pitch, shared.mOrientation[ Ams2Constants.VecX ] );
		dataSource.SetFloat( varTable.Roll, shared.mOrientation[ Ams2Constants.VecZ ] );

		// steering. no shaft torque exists in the map, so the torque samples stay zero.

		var steeringRangeRadians = GetSteeringRangeRadians();

		dataSource.SetFloat( varTable.SteeringWheelAngle, ComputeSteeringWheelAngle( shared.mUnfilteredSteering, steeringRangeRadians ) );
		dataSource.SetFloat( varTable.SteeringWheelAngleMax, steeringRangeRadians );
		dataSource.SetBool( varTable.SteeringFFBEnabled, false );

		for ( var i = 0; i < SamplesPerFrame; i++ )
		{
			dataSource.SetFloat( varTable.SteeringWheelTorque_ST, _torqueSamples[ i ], i );
		}

		dataSource.SetFloat( varTable.SteeringWheelTorque, _torqueSamples[ SamplesPerFrame - 1 ] );

		for ( var i = 0; i < SamplesPerFrame; i++ )
		{
			dataSource.SetFloat( varTable.LFshockVel_ST, _shockSamples[ 0, i ], i );
			dataSource.SetFloat( varTable.RFshockVel_ST, _shockSamples[ 1, i ], i );
			dataSource.SetFloat( varTable.LRshockVel_ST, _shockSamples[ 2, i ], i );
			dataSource.SetFloat( varTable.RRshockVel_ST, _shockSamples[ 3, i ], i );
		}

		// fuel

		var fuelLeft = FuelLiters( shared );

		dataSource.SetFloat( varTable.FuelLevel, fuelLeft );
		dataSource.SetFloat( varTable.FuelLevelPct, ( shared.mFuelCapacity > 0f ) ? fuelLeft / shared.mFuelCapacity : Math.Clamp( shared.mFuelLevel, 0f, 1f ) );
		dataSource.SetFloat( varTable.FuelUsePerHour, _fuelUsePerHour );

		// session and lap state

		var trackLength = Math.Max( 1f, shared.mTrackLength );
		var completedLaps = (int) player.mLapsCompleted;
		var lap = ( player.mCurrentLap > 0 ) ? (int) player.mCurrentLap : completedLaps + 1;
		var lapDist = Math.Max( 0f, player.mCurrentLapDistance );

		dataSource.SetDouble( varTable.SessionTime, Math.Max( 0.0, _sessionTime ) );
		dataSource.SetDouble( varTable.SessionTimeRemain, ( shared.mEventTimeRemaining >= 0f ) ? shared.mEventTimeRemaining / 1000.0 : IRacingSdkConst.UnlimitedTime );
		dataSource.SetInt( varTable.SessionNum, (int) shared.mSessionState );
		dataSource.SetInt( varTable.SessionState, MapSessionState( shared ) );
		dataSource.SetBitField( varTable.SessionFlags, MapFlagColour( shared.mHighestFlagColour ) );
		dataSource.SetInt( varTable.SessionLapsRemainEx, ( shared.mLapsInEvent > 0 ) ? Math.Max( 0, (int) shared.mLapsInEvent - completedLaps ) : IRacingSdkConst.UnlimitedLaps );

		dataSource.SetInt( varTable.Lap, Math.Max( 1, lap ) );
		dataSource.SetFloat( varTable.LapDist, lapDist );
		dataSource.SetFloat( varTable.LapDistPct, Math.Clamp( lapDist / trackLength, 0f, 1f ) );
		dataSource.SetFloat( varTable.LapBestLapTime, PositiveTime( ( shared.mPersonalFastestLapTime > 0f ) ? shared.mPersonalFastestLapTime : shared.mBestLapTime ) );
		dataSource.SetFloat( varTable.LapLastLapTime, PositiveTime( shared.mLastLapTime ) );

		// player state

		var engineActive = ( shared.mCarFlags & (uint) Ams2CarFlags.EngineActive ) != 0;

		dataSource.SetBool( varTable.IsOnTrack, onTrack );
		dataSource.SetBool( varTable.OnPitRoad, IsPitLane( pitMode ) );
		dataSource.SetBool( varTable.PitsOpen, (Ams2YellowFlagState) shared.mYellowFlagState != Ams2YellowFlagState.PitsClosed );
		dataSource.SetInt( varTable.PlayerCarIdx, Math.Max( 0, playerIndex ) );
		dataSource.SetInt( varTable.PlayerCarPosition, (int) player.mRacePosition );
		dataSource.SetInt( varTable.PlayerCarClassPosition, ( playerIndex >= 0 ) ? ClassPosition( shared, playerIndex ) : 0 );
		dataSource.SetInt( varTable.PlayerCarMyIncidentCount, 0 );
		dataSource.SetInt( varTable.PlayerTrackSurface, MapTrackSurface( shared ) );
		dataSource.SetInt( varTable.PlayerTrackSurfaceMaterial, MapMaterial( shared.mTerrain[ Ams2Constants.TyreFrontLeft ] ) );
		dataSource.SetBitField( varTable.EngineWarnings, ( onTrack && !engineActive && ( shared.mRpm < 10f ) ) ? (uint) IRacingSdkEnum.EngineWarnings.EngineStalled : 0u );

		dataSource.SetBool( varTable.WeatherDeclaredWet, ( shared.mRainDensity > 0.02f ) || ( shared.mSnowDensity > 0.02f ) );

		dataSource.SetInt( varTable.DisplayUnits, 1 );
		dataSource.SetFloat( varTable.FrameRate, _frameRate );
		dataSource.SetFloat( varTable.GpuUsage, 0f );
		dataSource.SetBool( varTable.IsReplayPlaying, (Ams2GameState) shared.mGameState is Ams2GameState.IngameReplay or Ams2GameState.FrontEndReplay );
		dataSource.SetBool( varTable.LoadNumTextures, false );
		dataSource.SetInt( varTable.PaceMode, (int) IRacingSdkEnum.PaceMode.NotPacing );
		dataSource.SetInt( varTable.CarLeftRight, (int) IRacingSdkEnum.CarLeftRight.Off );
		dataSource.SetInt( varTable.RadioTransmitCarIdx, -1 );
		dataSource.SetInt( varTable.ReplayFrameNumEnd, 0 );
		dataSource.SetBool( varTable.ReplayPlaySlowMotion, false );
		dataSource.SetInt( varTable.ReplayPlaySpeed, 1 );
		dataSource.SetFloat( varTable.TireLF_RumblePitch, RumblePitch( shared.mTerrain[ Ams2Constants.TyreFrontLeft ] ) );
		dataSource.SetFloat( varTable.TireRF_RumblePitch, RumblePitch( shared.mTerrain[ Ams2Constants.TyreFrontRight ] ) );
		dataSource.SetFloat( varTable.TireLR_RumblePitch, RumblePitch( shared.mTerrain[ Ams2Constants.TyreRearLeft ] ) );
		dataSource.SetFloat( varTable.TireRR_RumblePitch, RumblePitch( shared.mTerrain[ Ams2Constants.TyreRearRight ] ) );

		WriteCarIdxArrays( shared, playerIndex, trackLength, lapDist );
	}

	private void WriteCarIdxArrays( in Ams2Shared shared, int playerIndex, float trackLength, float playerLapDist )
	{
		var varTable = _varTable!;
		var dataSource = varTable.DataSource;

		var count = Math.Clamp( shared.mNumParticipants, 0, GameBridgeVarTable.MaxNumCars );

		var carDistAhead = float.MaxValue;
		var carDistBehind = float.MaxValue;

		Array.Clear( _carIdxFloatScratch );

		for ( var i = 0; i < count; i++ )
		{
			if ( shared.mParticipantInfo[ i ].mIsActive != 0 )
			{
				_carIdxFloatScratch[ i ] = PositiveTime( shared.mFastestLapTimes[ i ] );
			}
		}

		dataSource.SetFloatArray( varTable.CarIdxBestLapTime, _carIdxFloatScratch, 0, GameBridgeVarTable.MaxNumCars );

		Array.Clear( _carIdxFloatScratch );

		if ( playerIndex >= 0 )
		{
			_carIdxFloatScratch[ playerIndex ] = PositiveTime( shared.mCurrentTime );
		}

		dataSource.SetFloatArray( varTable.CarIdxEstTime, _carIdxFloatScratch, 0, GameBridgeVarTable.MaxNumCars );

		Array.Clear( _carIdxFloatScratch );
		dataSource.SetFloatArray( varTable.CarIdxF2Time, _carIdxFloatScratch, 0, GameBridgeVarTable.MaxNumCars );

		Array.Clear( _carIdxIntScratch );

		for ( var i = 0; i < count; i++ )
		{
			var participant = shared.mParticipantInfo[ i ];

			if ( participant.mIsActive != 0 )
			{
				_carIdxIntScratch[ i ] = ( participant.mCurrentLap > 0 ) ? (int) participant.mCurrentLap : (int) participant.mLapsCompleted + 1;
			}
		}

		dataSource.SetIntArray( varTable.CarIdxLap, _carIdxIntScratch, 0, GameBridgeVarTable.MaxNumCars );

		Array.Clear( _carIdxIntScratch );

		for ( var i = 0; i < count; i++ )
		{
			if ( shared.mParticipantInfo[ i ].mIsActive != 0 )
			{
				_carIdxIntScratch[ i ] = (int) shared.mParticipantInfo[ i ].mLapsCompleted;
			}
		}

		dataSource.SetIntArray( varTable.CarIdxLapCompleted, _carIdxIntScratch, 0, GameBridgeVarTable.MaxNumCars );

		Array.Clear( _carIdxFloatScratch );

		for ( var i = 0; i < count; i++ )
		{
			var participant = shared.mParticipantInfo[ i ];

			if ( participant.mIsActive == 0 )
			{
				continue;
			}

			var driverLapDist = Math.Max( 0f, participant.mCurrentLapDistance );

			_carIdxFloatScratch[ i ] = Math.Clamp( driverLapDist / trackLength, 0f, 1f );

			if ( ( i != playerIndex ) && !IsPitLane( (Ams2PitMode) shared.mPitModes[ i ] ) )
			{
				var distance = driverLapDist - playerLapDist;

				if ( distance < -trackLength * 0.5f )
				{
					distance += trackLength;
				}
				else if ( distance > trackLength * 0.5f )
				{
					distance -= trackLength;
				}

				if ( distance >= 0f )
				{
					carDistAhead = Math.Min( carDistAhead, distance );
				}
				else
				{
					carDistBehind = Math.Min( carDistBehind, -distance );
				}
			}
		}

		dataSource.SetFloatArray( varTable.CarIdxLapDistPct, _carIdxFloatScratch, 0, GameBridgeVarTable.MaxNumCars );

		dataSource.SetFloat( varTable.CarDistAhead, ( carDistAhead == float.MaxValue ) ? 999999f : carDistAhead );
		dataSource.SetFloat( varTable.CarDistBehind, ( carDistBehind == float.MaxValue ) ? 999999f : carDistBehind );

		Array.Clear( _carIdxIntScratch );

		for ( var i = 0; i < count; i++ )
		{
			if ( shared.mParticipantInfo[ i ].mIsActive != 0 )
			{
				_carIdxIntScratch[ i ] = (int) shared.mParticipantInfo[ i ].mRacePosition;
			}
		}

		dataSource.SetIntArray( varTable.CarIdxPosition, _carIdxIntScratch, 0, GameBridgeVarTable.MaxNumCars );

		for ( var i = 0; i < GameBridgeVarTable.MaxNumCars; i++ )
		{
			var onPitRoad = ( i < count ) && ( shared.mParticipantInfo[ i ].mIsActive != 0 ) && IsPitLane( (Ams2PitMode) shared.mPitModes[ i ] );

			dataSource.SetBool( varTable.CarIdxOnPitRoad, onPitRoad, i );
			dataSource.SetBitField( varTable.CarIdxSessionFlags, ( ( i < count ) && ( shared.mParticipantInfo[ i ].mIsActive != 0 ) ) ? MapFlagColour( shared.mHighestFlagColours[ i ] ) : 0u, i );
		}

		Array.Clear( _carIdxIntScratch );

		if ( playerIndex >= 0 )
		{
			_carIdxIntScratch[ playerIndex ] = HashName( shared.mTyreCompound[ Ams2Constants.TyreFrontLeft ] );
		}

		dataSource.SetIntArray( varTable.CarIdxTireCompound, _carIdxIntScratch, 0, GameBridgeVarTable.MaxNumCars );
	}

	private static int PlayerIndex( in Ams2Shared shared )
	{
		var count = Math.Clamp( shared.mNumParticipants, 0, Ams2Constants.StoredParticipantsMax );
		var index = shared.mViewedParticipantIndex;

		if ( ( index < 0 ) || ( index >= count ) )
		{
			return -1;
		}

		return index;
	}

	private static bool IsOnTrack( in Ams2Shared shared, int playerIndex, Ams2PitMode pitMode )
	{
		if ( playerIndex < 0 )
		{
			return false;
		}

		var gameState = (Ams2GameState) shared.mGameState;

		if ( gameState is not ( Ams2GameState.IngamePlaying or Ams2GameState.IngamePaused or Ams2GameState.IngameInmenuTimeTicking ) )
		{
			return false;
		}

		return pitMode is not ( Ams2PitMode.InGarage or Ams2PitMode.DrivingOutOfGarage );
	}

	private static bool IsPitLane( Ams2PitMode pitMode )
	{
		return pitMode is Ams2PitMode.DrivingIntoPits or Ams2PitMode.InPit or Ams2PitMode.DrivingOutOfPits;
	}

	private static int ClassPosition( in Ams2Shared shared, int playerIndex )
	{
		var playerPosition = shared.mParticipantInfo[ playerIndex ].mRacePosition;

		if ( playerPosition == 0 )
		{
			return 0;
		}

		var playerClass = HashName( shared.mCarClassNames[ playerIndex ] );
		var position = 1;
		var count = Math.Clamp( shared.mNumParticipants, 0, Ams2Constants.StoredParticipantsMax );

		for ( var i = 0; i < count; i++ )
		{
			if ( i == playerIndex )
			{
				continue;
			}

			var participant = shared.mParticipantInfo[ i ];

			if ( ( participant.mIsActive == 0 ) || ( participant.mRacePosition == 0 ) || ( participant.mRacePosition >= playerPosition ) )
			{
				continue;
			}

			if ( HashName( shared.mCarClassNames[ i ] ) == playerClass )
			{
				position++;
			}
		}

		return position;
	}

	private static float PositiveTime( float seconds )
	{
		return ( seconds > 0f ) ? seconds : 0f;
	}

	private static float RumblePitch( uint terrain )
	{
		return IsRumble( (Ams2Terrain) terrain ) ? RumblePitchOnStrip : 0f;
	}

	private static bool IsRumble( Ams2Terrain terrain )
	{
		return terrain is Ams2Terrain.RumbleStrips or Ams2Terrain.ExitRumbleStrips or Ams2Terrain.B1Rumbles or Ams2Terrain.B2Rumbles;
	}

	private static uint MapFlagColour( uint colour )
	{
		return (Ams2FlagColour) colour switch
		{
			Ams2FlagColour.Chequered => 0x00000001,
			Ams2FlagColour.WhiteSlowCar or Ams2FlagColour.WhiteFinalLap => 0x00000002,
			Ams2FlagColour.Green => 0x00000004,
			Ams2FlagColour.Yellow => 0x00000008,
			Ams2FlagColour.DoubleYellow => 0x00000008 | 0x00000100,
			Ams2FlagColour.Red => 0x00000010,
			Ams2FlagColour.Blue => 0x00000020,
			Ams2FlagColour.BlackAndWhite => 0x00010000,
			Ams2FlagColour.BlackOrangeCircle => 0x00100000,
			Ams2FlagColour.Black => 0x00020000,
			_ => 0u
		};
	}

	private static int MapSessionState( in Ams2Shared shared )
	{
		var gameState = (Ams2GameState) shared.mGameState;

		if ( gameState is Ams2GameState.Exited or Ams2GameState.FrontEnd or Ams2GameState.FrontEndReplay )
		{
			return (int) IRacingSdkEnum.SessionState.Invalid;
		}

		if ( (Ams2PitMode) shared.mPitMode is Ams2PitMode.InGarage or Ams2PitMode.DrivingOutOfGarage )
		{
			return (int) IRacingSdkEnum.SessionState.GetInCar;
		}

		if ( (Ams2SessionState) shared.mSessionState == Ams2SessionState.FormationLap )
		{
			return (int) IRacingSdkEnum.SessionState.ParadeLaps;
		}

		if ( ( (Ams2RaceState) shared.mRaceState == Ams2RaceState.Finished ) || ( (Ams2FlagColour) shared.mHighestFlagColour == Ams2FlagColour.Chequered ) )
		{
			return (int) IRacingSdkEnum.SessionState.Checkered;
		}

		return (int) IRacingSdkEnum.SessionState.Racing;
	}

	private static int MapTrackSurface( in Ams2Shared shared )
	{
		var gameState = (Ams2GameState) shared.mGameState;

		if ( gameState is Ams2GameState.Exited or Ams2GameState.FrontEnd or Ams2GameState.FrontEndReplay )
		{
			return (int) IRacingSdkEnum.TrkLoc.NotInWorld;
		}

		return (Ams2PitMode) shared.mPitMode switch
		{
			Ams2PitMode.InGarage or Ams2PitMode.InPit => (int) IRacingSdkEnum.TrkLoc.InPitStall,
			Ams2PitMode.DrivingIntoPits or Ams2PitMode.DrivingOutOfPits or Ams2PitMode.DrivingOutOfGarage => (int) IRacingSdkEnum.TrkLoc.AproachingPits,
			_ => (int) IRacingSdkEnum.TrkLoc.OnTrack
		};
	}

	private static int MapMaterial( uint terrain )
	{
		// unknown materials report asphalt so MAIRA's auto max force stays enabled (a real material is
		// required or the peak-torque gate silently disables it - the LMU lesson)
		return (Ams2Terrain) terrain switch
		{
			Ams2Terrain.Grass or Ams2Terrain.GrassyBerms or Ams2Terrain.LongGrass or Ams2Terrain.SlopeGrass or Ams2Terrain.DryVerge or Ams2Terrain.Astroturf or Ams2Terrain.Grasscrete => (int) IRacingSdkEnum.TrkSurf.Grass1Material,
			Ams2Terrain.Gravel or Ams2Terrain.BumpyGravel => (int) IRacingSdkEnum.TrkSurf.Gravel1Material,
			Ams2Terrain.Dirt or Ams2Terrain.BumpyDirt or Ams2Terrain.DirtRoad or Ams2Terrain.BumpyDirtRoad or Ams2Terrain.DirtBank or Ams2Terrain.Sand or Ams2Terrain.BumpySand or Ams2Terrain.SandRoad or Ams2Terrain.RoughSandMedium or Ams2Terrain.RoughSandHeavy or Ams2Terrain.BakedClay => (int) IRacingSdkEnum.TrkSurf.Dirt1Material,
			Ams2Terrain.RumbleStrips or Ams2Terrain.ExitRumbleStrips or Ams2Terrain.B1Rumbles or Ams2Terrain.B2Rumbles => (int) IRacingSdkEnum.TrkSurf.Rumble1Material,
			Ams2Terrain.CementWalls or Ams2Terrain.Pavement or Ams2Terrain.PaintConcrete or Ams2Terrain.PaintConcreteIllegal or Ams2Terrain.Cobbles or Ams2Terrain.BumpyCobbles => (int) IRacingSdkEnum.TrkSurf.Concrete1Material,
			_ => (int) IRacingSdkEnum.TrkSurf.Asphalt1Material
		};
	}

	#endregion

	#region session info

	private void UpdateSessionInfo( double pumpSeconds )
	{
		if ( pumpSeconds - _lastSessionInfoUpdateTime < 1.0 )
		{
			return;
		}

		_lastSessionInfoUpdateTime = pumpSeconds;

		var shared = _shared;
		var playerIndex = PlayerIndex( shared );
		var trackName = ReadString( shared.mTrackLocation );
		var layoutName = ReadString( shared.mTrackVariation );
		var playerCarName = ( playerIndex >= 0 ) ? ReadString( shared.mCarNames[ playerIndex ] ) : ReadString( shared.mCarName );

		if ( playerCarName.Length == 0 )
		{
			playerCarName = ReadString( shared.mCarName );
		}

		var signature = $"{trackName}|{layoutName}|{playerCarName}|{shared.mSessionState}|{shared.mNumParticipants}|{(int) shared.mMaxRPM}|{playerIndex}";

		if ( signature == _lastSessionInfoSignature )
		{
			return;
		}

		_lastSessionInfoSignature = signature;

		var maxRpm = Math.Max( 0f, shared.mMaxRPM );

		var builder = new GameBridgeSessionInfoBuilder
		{
			TrackDisplayName = trackName,
			TrackConfigName = layoutName,
			TrackLengthInKm = Math.Max( 0f, shared.mTrackLength ) / 1000f,
			SeriesID = 0,
			LeagueID = 0,
			SessionID = (int) shared.mSessionState,
			TimeOfDay = "12:00 pm",
			DriverCarIdx = Math.Max( 0, playerIndex ),
			DriverSetupName = "bridge",
			DriverCarGearNumForward = ( shared.mNumGears > 0 ) ? shared.mNumGears : 6,
			DriverCarRedLine = maxRpm,
			DriverCarSLFirstRPM = maxRpm * 0.88f,
			DriverCarSLShiftRPM = maxRpm * 0.96f,
			DriverCarSLBlinkRPM = maxRpm * 0.98f
		};

		var count = Math.Clamp( shared.mNumParticipants, 0, GameBridgeVarTable.MaxNumCars );

		for ( var i = 0; i < count; i++ )
		{
			if ( shared.mParticipantInfo[ i ].mIsActive == 0 )
			{
				continue;
			}

			var carName = ReadString( shared.mCarNames[ i ] );

			if ( carName.Length == 0 )
			{
				carName = ReadString( shared.mCarClassNames[ i ] );
			}

			builder.Drivers.Add( new GameBridgeSessionInfoBuilder.DriverModel
			{
				CarIdx = i,
				UserName = ReadString( shared.mParticipantInfo[ i ].mName ),
				UserID = i + 1,
				CarNumber = ( i + 1 ).ToString(),
				CarScreenName = carName,
				CarPath = ReadString( shared.mCarClassNames[ i ] ),
				CarClassID = HashName( shared.mCarClassNames[ i ] ),
				CarIsPaceCar = 0,
				IRating = 0,
				IsSpectator = 0,
				TeamID = 0
			} );
		}

		builder.Sessions.Add( new GameBridgeSessionInfoBuilder.SessionModel
		{
			SessionNum = (int) shared.mSessionState,
			SessionType = MapSessionType( (Ams2SessionState) shared.mSessionState )
		} );

		_varTable!.DataSource.SetSessionInfo( builder.ToYaml() );
	}

	private static string MapSessionType( Ams2SessionState sessionState )
	{
		return sessionState switch
		{
			Ams2SessionState.Race or Ams2SessionState.FormationLap => "Race",
			Ams2SessionState.Qualify => "Lone Qualify",
			_ => "Practice"
		};
	}

	#endregion
}
