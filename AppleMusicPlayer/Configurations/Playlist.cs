namespace AppleMusicPlayer.Configurations
{
	internal class Playlist
	{
		public int WaitForOpen { get; set; } = 0;
		public bool IsShuffleOverPlay { get; set; } = false;
		public Dictionary<string, Dictionary<string, string>> Schedules { get; set; } = new();
	}
}
