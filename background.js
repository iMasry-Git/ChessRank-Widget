// ChessRank Widget Background Service Worker (Manifest V3)
chrome.runtime.onInstalled.addListener(() => {
  chrome.alarms.create("chessrank_sync", { periodInMinutes: 15 });
});

chrome.alarms.onAlarm.addListener(async (alarm) => {
  if (alarm.name === "chessrank_sync") {
    try {
      const data = await chrome.storage.local.get(["username", "timeControl"]);
      const username = data.username || "GRANDMASTER_USER";
      const timeControl = data.timeControl || "chess_rapid";

      if (username && username !== "GRANDMASTER_USER") {
        const res = await fetch(`https://api.chess.com/pub/player/${encodeURIComponent(username.toLowerCase())}/stats`, {
          headers: { 'User-Agent': 'ChessRankWidget/1.0.0 (contact: info@chessrank.app)' }
        });
        if (res.ok) {
          const stats = await res.json();
          const modeStats = stats[timeControl] || stats.chess_rapid || stats.chess_blitz;
          if (modeStats && modeStats.last) {
            const currentRating = modeStats.last.rating;
            chrome.action.setBadgeText({ text: currentRating.toString() });
            chrome.action.setBadgeBackgroundColor({ color: "#5bb83c" });
            await chrome.storage.local.set({
              cachedStats: stats,
              lastSynced: Date.now()
            });
          }
        }
      }
    } catch (err) {
      console.warn("Background sync error:", err);
    }
  }
});
