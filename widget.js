// ChessRank Widget v2.1.1 - Core Logic & Settings Management
(function () {
  'use strict';

  // DOM Elements - Widget
  const usernameDisplay = document.getElementById('usernameDisplay');
  const modeBadge = document.getElementById('modeBadge');
  const ratingDisplay = document.getElementById('ratingDisplay');
  const bubbleToday = document.getElementById('bubbleToday');
  const deltaDisplay = document.getElementById('deltaDisplay');
  const peakDisplay = document.getElementById('peakDisplay');
  const progressFill = document.getElementById('progressFill');
  const progressTrack = document.getElementById('progressTrack');
  const progressCurrent = document.getElementById('progressCurrent');
  const progressRemaining = document.getElementById('progressRemaining');

  // DOM Elements - Modal & Form
  const modalBackdrop = document.getElementById('modalBackdrop');
  const btnSettings = document.getElementById('btnSettings');
  const btnCloseModal = document.getElementById('btnCloseModal');
  const btnCancelModal = document.getElementById('btnCancelModal');
  const btnSaveConfig = document.getElementById('btnSaveConfig');
  const btnResetDemo = document.getElementById('btnResetDemo');
  const saveToast = document.getElementById('saveToast');

  const inputUsername = document.getElementById('inputUsername');
  const selectTimeControl = document.getElementById('selectTimeControl');
  const selectRefreshInterval = document.getElementById('selectRefreshInterval');
  const inputTargetRank = document.getElementById('inputTargetRank');
  const inputBaselineRank = document.getElementById('inputBaselineRank');
  const chkSound = document.getElementById('chkSound');

  // Default Mockup State
  const DEFAULT_CONFIG = {
    username: 'GRANDMASTER_USER',
    timeControl: 'chess_rapid',
    targetRank: 2200,
    baselineRank: 2050,
    refreshInterval: 1800, // 30 minutes
    sound: true,
    lastRating: 2145,
    lastPeak: 2210,
    lastDelta: 12
  };

  let config = { ...DEFAULT_CONFIG };
  let refreshTimer = null;
  let audioCtx = null;

  // Cross-environment Storage (Chrome Extension / Browser localStorage)
  async function loadConfig() {
    try {
      if (typeof chrome !== 'undefined' && chrome.storage && chrome.storage.local) {
        return new Promise((resolve) => {
          chrome.storage.local.get(['chessRankConfig'], (result) => {
            if (result && result.chessRankConfig) {
              resolve({ ...DEFAULT_CONFIG, ...result.chessRankConfig });
            } else {
              resolve({ ...DEFAULT_CONFIG });
            }
          });
        });
      }
    } catch (e) {}

    try {
      const saved = localStorage.getItem('chessRankConfig');
      if (saved) {
        return { ...DEFAULT_CONFIG, ...JSON.parse(saved) };
      }
    } catch (e) {}

    return { ...DEFAULT_CONFIG };
  }

  async function saveConfig(newConfig) {
    config = { ...config, ...newConfig };
    try {
      if (typeof chrome !== 'undefined' && chrome.storage && chrome.storage.local) {
        chrome.storage.local.set({ chessRankConfig: config });
      }
    } catch (e) {}

    try {
      localStorage.setItem('chessRankConfig', JSON.stringify(config));
    } catch (e) {}
  }

  // Audio Cue using Web Audio API
  function playChime(type) {
    if (!config.sound) return;
    try {
      if (!audioCtx) {
        const AudioContextClass = window.AudioContext || window.webkitAudioContext;
        if (AudioContextClass) audioCtx = new AudioContextClass();
      }
      if (!audioCtx) return;
      if (audioCtx.state === 'suspended') audioCtx.resume();

      const now = audioCtx.currentTime;
      const osc = audioCtx.createOscillator();
      const gain = audioCtx.createGain();
      osc.connect(gain);
      gain.connect(audioCtx.destination);

      if (type === 'goal') {
        osc.type = 'triangle';
        osc.frequency.setValueAtTime(523.25, now);
        osc.frequency.setValueAtTime(659.25, now + 0.1);
        osc.frequency.setValueAtTime(783.99, now + 0.2);
        osc.frequency.setValueAtTime(1046.50, now + 0.35);
        gain.gain.setValueAtTime(0.2, now);
        gain.gain.exponentialRampToValueAtTime(0.001, now + 0.8);
        osc.start(now);
        osc.stop(now + 0.8);
      } else {
        osc.type = 'sine';
        osc.frequency.setValueAtTime(587.33, now);
        osc.frequency.setValueAtTime(880.00, now + 0.08);
        gain.gain.setValueAtTime(0.15, now);
        gain.gain.exponentialRampToValueAtTime(0.001, now + 0.4);
        osc.start(now);
        osc.stop(now + 0.4);
      }
    } catch (e) {}
  }

  function getModeLabel(key) {
    switch (key) {
      case 'chess_rapid': return 'RAPID';
      case 'chess_blitz': return 'BLITZ';
      case 'chess_bullet': return 'BULLET';
      case 'chess_daily': return 'DAILY';
      default: return 'RATING';
    }
  }

  // Render Widget UI
  function renderWidget(rating, peak, delta) {
    usernameDisplay.textContent = config.username;
    modeBadge.textContent = getModeLabel(config.timeControl);

    // Rating Number
    ratingDisplay.textContent = rating;

    // Delta Bubble
    const formattedDelta = delta > 0 ? `+${delta}` : `${delta}`;
    deltaDisplay.textContent = formattedDelta;

    bubbleToday.classList.remove('negative', 'neutral');
    if (delta < 0) {
      bubbleToday.classList.add('negative');
    } else if (delta === 0) {
      bubbleToday.classList.add('neutral');
    }

    // Historical Peak
    peakDisplay.textContent = peak;

    // Progress Bar
    const target = Number(config.targetRank) > 0 ? Number(config.targetRank) : 2200;
    let baseline = Number(config.baselineRank);
    // Respect 0 as valid baseline!
    if (isNaN(baseline) || baseline < 0 || baseline >= target) {
      baseline = Math.max(0, target - 150);
    }

    let progressPercent = 0;
    if (rating >= target) {
      progressPercent = 100;
    } else if (rating <= baseline) {
      progressPercent = 4;
    } else {
      progressPercent = Math.round(((rating - baseline) / (target - baseline)) * 100);
      progressPercent = Math.max(4, Math.min(100, progressPercent));
    }

    progressFill.style.width = `${progressPercent}%`;

    const remaining = Math.max(0, target - rating);
    progressCurrent.textContent = `Goal ${target}`;

    if (remaining === 0) {
      progressRemaining.textContent = `Goal reached`;
      progressRemaining.style.color = 'var(--green-rating)';
    } else {
      progressRemaining.textContent = `${remaining} to go`;
      progressRemaining.style.color = 'var(--text-muted)';
    }
  }

  // Fetch Live Chess.com Data
  async function fetchChessData() {
    const username = config.username.trim();

    if (username.toUpperCase() === 'GRANDMASTER_USER') {
      renderWidget(config.lastRating, config.lastPeak, config.lastDelta);
      return;
    }

    ratingDisplay.style.opacity = '0.6';

    try {
      const headers = { 'User-Agent': 'ChessRankWidget/2.1.1 (Windows 11 Native Widget; support@chessrank.app)' };
      const statsUrl = `https://api.chess.com/pub/player/${encodeURIComponent(username.toLowerCase())}/stats`;
      const statsResp = await fetch(statsUrl, { headers });

      if (!statsResp.ok) throw new Error(`User not found (${statsResp.status})`);

      const stats = await statsResp.json();
      const modeKey = config.timeControl;
      const modeData = stats[modeKey] || stats.chess_rapid || stats.chess_daily || stats.chess_blitz || {};

      const currentRating = modeData.last ? modeData.last.rating : 1200;
      const peakRating = modeData.best ? modeData.best.rating : currentRating;

      let deltaToday = 0;
      try {
        const now = new Date();
        const year = now.getFullYear();
        const month = String(now.getMonth() + 1).padStart(2, '0');
        const gamesUrl = `https://api.chess.com/pub/player/${encodeURIComponent(username.toLowerCase())}/games/${year}/${month}`;

        const gamesResp = await fetch(gamesUrl, { headers });
        if (gamesResp.ok) {
          const gamesData = await gamesResp.json();
          const games = gamesData.games || [];
          const todayDateStr = now.toISOString().slice(0, 10);
          const timeCategory = modeKey.replace('chess_', '');

          const todayGames = games.filter(g => {
            const gameDateStr = new Date(g.end_time * 1000).toISOString().slice(0, 10);
            return gameDateStr === todayDateStr && (g.time_class === timeCategory);
          });

          if (todayGames.length > 0) {
            const uLower = username.toLowerCase();
            let netDiff = 0;
            todayGames.forEach(g => {
              const isW = (g.white.username || '').toLowerCase() === uLower;
              const result = isW ? g.white.result : (g.black ? g.black.result : '');
              if (result === 'win') netDiff += 8;
              else if (result === 'resigned' || result === 'checkmated' || result === 'timeout' || result === 'abandoned') netDiff -= 8;
            });
            deltaToday = netDiff;
          }
        }
      } catch (e) {}

      ratingDisplay.style.opacity = '1.0';

      if (currentRating > config.lastRating) {
        playChime(currentRating >= config.targetRank ? 'goal' : 'increase');
      }

      config.lastRating = currentRating;
      config.lastPeak = peakRating;
      config.lastDelta = deltaToday;
      saveConfig(config);

      renderWidget(currentRating, peakRating, deltaToday);
    } catch (err) {
      ratingDisplay.style.opacity = '1.0';
      renderWidget(config.lastRating, config.lastPeak, config.lastDelta);
    }
  }

  function populateModal() {
    inputUsername.value = config.username;
    selectTimeControl.value = config.timeControl;
    selectRefreshInterval.value = String(config.refreshInterval);
    inputTargetRank.value = config.targetRank;
    inputBaselineRank.value = config.baselineRank !== undefined ? config.baselineRank : '';
    chkSound.checked = Boolean(config.sound);
  }

  function openModal() {
    populateModal();
    saveToast.classList.remove('show');
    modalBackdrop.classList.add('open');
    inputUsername.focus();
  }

  function closeModal() {
    modalBackdrop.classList.remove('open');
  }

  async function handleSave() {
    const newUsername = inputUsername.value.trim() || 'GRANDMASTER_USER';
    const newTarget = parseInt(inputTargetRank.value, 10) || 2200;
    const rawBaseline = parseInt(inputBaselineRank.value, 10);
    const newBaseline = isNaN(rawBaseline) ? Math.max(0, newTarget - 150) : rawBaseline;
    const newInterval = parseInt(selectRefreshInterval.value, 10);
    const newMode = selectTimeControl.value;
    const newSound = chkSound.checked;

    config.username = newUsername;
    config.targetRank = newTarget;
    config.baselineRank = newBaseline;
    config.refreshInterval = newInterval;
    config.timeControl = newMode;
    config.sound = newSound;

    await saveConfig(config);

    // Instant UI refresh
    renderWidget(config.lastRating, config.lastPeak, config.lastDelta);

    saveToast.textContent = "✓ Settings Saved & Active!";
    saveToast.classList.add('show');
    setTimeout(() => {
      saveToast.classList.remove('show');
      closeModal();
    }, 400);

    setupTimer();
    fetchChessData();
  }

  function setupTimer() {
    if (refreshTimer) clearInterval(refreshTimer);
    const intervalSec = Number(config.refreshInterval) || 0;
    if (intervalSec > 0) {
      refreshTimer = setInterval(fetchChessData, intervalSec * 1000);
    }
  }

  function initEvents() {
    ratingDisplay.addEventListener('click', () => {
      const u = config.username.trim();
      if (u && u.toUpperCase() !== 'GRANDMASTER_USER') {
        window.open(`https://www.chess.com/member/${encodeURIComponent(u)}`, '_blank');
      } else {
        window.open('https://www.chess.com', '_blank');
      }
    });

    btnSettings.addEventListener('click', openModal);
    btnCloseModal.addEventListener('click', closeModal);
    btnCancelModal.addEventListener('click', closeModal);
    btnSaveConfig.addEventListener('click', handleSave);

    modalBackdrop.addEventListener('click', (e) => {
      if (e.target === modalBackdrop) closeModal();
    });

    btnResetDemo.addEventListener('click', () => {
      config = { ...DEFAULT_CONFIG };
      saveConfig(config);
      populateModal();
      renderWidget(config.lastRating, config.lastPeak, config.lastDelta);
      setupTimer();
      saveToast.textContent = "✓ Reset to Mockup!";
      saveToast.classList.add('show');
      setTimeout(() => {
        saveToast.classList.remove('show');
        closeModal();
      }, 500);
    });

    window.addEventListener('keydown', (e) => {
      if (e.key === 'Escape' && modalBackdrop.classList.contains('open')) {
        closeModal();
      } else if (e.key === 'Enter' && modalBackdrop.classList.contains('open')) {
        handleSave();
      } else if ((e.key === 's' || e.key === 'S') && !['INPUT', 'SELECT'].includes(document.activeElement.tagName)) {
        if (modalBackdrop.classList.contains('open')) closeModal();
        else openModal();
      } else if ((e.key === 'r' || e.key === 'R') && !['INPUT', 'SELECT'].includes(document.activeElement.tagName)) {
        fetchChessData();
      }
    });

    document.addEventListener('contextmenu', (e) => {
      e.preventDefault();
      openModal();
    });
  }

  async function init() {
    config = await loadConfig();
    renderWidget(config.lastRating, config.lastPeak, config.lastDelta);
    initEvents();
    setupTimer();

    if (config.username && config.username.toUpperCase() !== 'GRANDMASTER_USER') {
      fetchChessData();
    }
  }

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', init);
  } else {
    init();
  }
})();
