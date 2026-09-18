/**
 * BOSTON TIMING SYSTEM • LIVE SCOREBOARD CONTROLLER
 * Connects directly to Desktop Timing WebSocket Server as Spectator role.
 * Renders real-time timers, split times, automatic rank badges, and finishes.
 */

class ScoreboardController {
  constructor() {
    this.ws = null;
    this.reconnectTimer = null;
    this.pingTimer = null;
    this.clockInterval = null;

    // Timing state
    this.timingMode = 'Pool'; // 'Pool' or 'OpenWater'
    this.raceStatus = 'Ready';
    this.elapsedMs = 0;
    this.lastTickTime = 0;
    this.isRunning = false;

    // Meet context
    this.meetName = 'Swimming Championship 2026';
    this.eventNumber = 1;
    this.eventName = '50m Freestyle';
    this.heatNumber = 1;

    // Lanes (0-9 / 1-10)
    this.lanes = [];
    this.owsRecords = [];

    // Cached DOM elements
    this.dom = {
      meetTitle: document.getElementById('txtMeetTitle'),
      badgeEvent: document.getElementById('badgeEvent'),
      eventName: document.getElementById('txtEventName'),
      badgeHeat: document.getElementById('badgeHeat'),
      badgeMode: document.getElementById('badgeMode'),
      masterTimer: document.getElementById('txtMasterTimer'),
      raceStatusTag: document.getElementById('tagRaceStatus'),
      connIndicator: document.getElementById('connIndicator'),
      connLabel: document.getElementById('txtConnLabel'),
      wsEndpoint: document.getElementById('txtWsEndpoint'),
      localTime: document.getElementById('txtLocalTime'),
      poolView: document.getElementById('poolView'),
      owsView: document.getElementById('owsView'),
      laneRowsContainer: document.getElementById('laneRowsContainer'),
      owsRowsContainer: document.getElementById('owsRowsContainer'),
      btnFullscreen: document.getElementById('btnFullscreen'),
      // OWS Stats
      owsTotal: document.getElementById('txtOwsTotal'),
      owsFinished: document.getElementById('txtOwsFinished'),
      owsOnCourse: document.getElementById('txtOwsOnCourse'),
      owsPenalized: document.getElementById('txtOwsPenalized'),
    };

    this.initLanes();
    this.initEvents();
    this.startDigitalClock();
    this.detectAndConnect();
  }

  initLanes() {
    this.lanes = [];
    for (let i = 1; i <= 10; i++) {
      this.lanes.push({
        laneNumber: i,
        swimmerName: '',
        club: '',
        splitTime: '',
        formattedTime: '00:00.00',
        finishTimeMs: null,
        rank: null,
        status: 'Ready'
      });
    }
    this.renderPoolLanes();
  }

  initEvents() {
    if (this.dom.btnFullscreen) {
      this.dom.btnFullscreen.addEventListener('click', () => {
        if (!document.fullscreenElement) {
          document.documentElement.requestFullscreen().catch(() => {});
        } else {
          document.exitFullscreen().catch(() => {});
        }
      });
    }

    // Keyboard hotkey for F11 or Space
    window.addEventListener('keydown', (e) => {
      if (e.key === 'F11') {
        e.preventDefault();
        if (!document.fullscreenElement) {
          document.documentElement.requestFullscreen().catch(() => {});
        } else {
          document.exitFullscreen().catch(() => {});
        }
      }
    });
  }

  startDigitalClock() {
    setInterval(() => {
      const now = new Date();
      if (this.dom.localTime) {
        this.dom.localTime.textContent = now.toTimeString().split(' ')[0];
      }
    }, 1000);

    // Smooth client-side timer interpolation if running (~30 FPS)
    setInterval(() => {
      if (this.isRunning && this.lastTickTime > 0) {
        const delta = performance.now() - this.lastTickTime;
        const currentElapsed = this.elapsedMs + delta;
        this.updateMasterDisplay(this.formatTime(currentElapsed));
      }
    }, 33);
  }

  async detectAndConnect() {
    // 1. First probe local API info endpoint if served from embedded ScoreboardWebServer
    let targetWsPort = 8181;
    const host = window.location.hostname || 'localhost';

    try {
      const resp = await fetch('/api/info', { cache: 'no-cache' });
      if (resp.ok) {
        const info = await resp.json();
        if (info && info.wsPort) {
          targetWsPort = info.wsPort;
        }
      }
    } catch (e) {
      // Running standalone or static, fall back to candidate port
    }

    const wsUrl = `ws://${host}:${targetWsPort}`;
    if (this.dom.wsEndpoint) {
      this.dom.wsEndpoint.textContent = wsUrl;
    }
    this.connectWebSocket(wsUrl);
  }

  connectWebSocket(url) {
    if (this.ws) {
      try { this.ws.close(); } catch (e) {}
    }

    this.setConnectionState(false, 'CONNECTING...');

    try {
      this.ws = new WebSocket(url);
    } catch (err) {
      this.scheduleReconnect(url);
      return;
    }

    this.ws.onopen = () => {
      this.setConnectionState(true, 'LIVE CONNECTED');
      // Register as spectator role (Spectator does not require access code)
      this.send({
        action: 'REGISTER',
        role: 'Spectator',
        deviceName: 'Scoreboard Display Web'
      });
      // Request initial state snapshot
      this.send({ action: 'SYNC_REQUEST' });

      // Keepalive heartbeat: send PING every 2.5 seconds to satisfy server 8s watchdog
      clearInterval(this.pingTimer);
      this.pingTimer = setInterval(() => {
        this.send({
          action: 'PING',
          clientTimestamp: Date.now()
        });
      }, 2500);
    };

    this.ws.onmessage = (event) => {
      try {
        const msg = JSON.parse(event.data);
        this.handleServerMessage(msg);
      } catch (err) {
        console.error('Error parsing WS message', err);
      }
    };

    this.ws.onclose = () => {
      clearInterval(this.pingTimer);
      this.setConnectionState(false, 'DISCONNECTED');
      this.scheduleReconnect(url);
    };

    this.ws.onerror = () => {
      clearInterval(this.pingTimer);
      this.setConnectionState(false, 'ERROR');
    };
  }

  scheduleReconnect(url) {
    clearTimeout(this.reconnectTimer);
    this.reconnectTimer = setTimeout(() => {
      this.connectWebSocket(url);
    }, 2500);
  }

  send(data) {
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.ws.send(JSON.stringify(data));
    }
  }

  setConnectionState(connected, label) {
    if (this.dom.connIndicator) {
      if (connected) {
        this.dom.connIndicator.classList.add('connected');
      } else {
        this.dom.connIndicator.classList.remove('connected');
      }
    }
    if (this.dom.connLabel) {
      this.dom.connLabel.textContent = label;
    }
  }

  handleServerMessage(msg) {
    const ev = (msg.event || '').toUpperCase();

    switch (ev) {
      case 'STATE_SYNC':
      case 'SYNC_RESPONSE':
      case 'INITIAL_STATE':
        this.applyStateSync(msg);
        break;

      case 'RACE_STARTED':
        this.isRunning = true;
        this.raceStatus = 'Running';
        this.elapsedMs = 0;
        this.lastTickTime = performance.now();
        this.updateStatusTag('RUNNING', 'running');
        this.updateMasterDisplay('00:00.00');
        this.updateAllLanesStatus('Running');
        break;

      case 'RACE_STOPPED':
        this.isRunning = false;
        this.raceStatus = 'Finished';
        this.updateStatusTag('FINISHED', 'finished');
        if (msg.elapsedFormatted) {
          this.updateMasterDisplay(msg.elapsedFormatted);
        }
        break;

      case 'RACE_RESET':
        this.isRunning = false;
        this.raceStatus = 'Ready';
        this.elapsedMs = 0;
        this.updateStatusTag('READY', '');
        this.updateMasterDisplay('00:00.00');
        this.resetLanesTimes();
        break;

      case 'TICK':
        if (msg.elapsedMs !== undefined) {
          this.elapsedMs = msg.elapsedMs;
          this.lastTickTime = performance.now();
        }
        if (msg.elapsedFormatted) {
          this.updateMasterDisplay(msg.elapsedFormatted);
        }
        break;

      case 'LANE_FINISHED':
      case 'LANE_FINISH':
        this.handleLaneFinished(msg);
        break;

      case 'LANE_SPLIT':
        this.handleLaneSplit(msg);
        break;

      case 'LANE_STATUS_CHANGED':
        this.handleLaneStatusChanged(msg);
        break;

      case 'MODE_CHANGED':
        if (msg.mode) {
          this.setTimingMode(msg.mode);
        }
        break;

      case 'OWS_FINISH_RECORDED':
      case 'OWS_RECORD_UPDATED':
      case 'OWS_RECORD_DELETED':
        if (msg.owsRecords) {
          this.owsRecords = msg.owsRecords;
          this.renderOwsView();
        }
        break;

      case 'MEET_CONTEXT_CHANGED':
        if (msg.meetName) this.meetName = msg.meetName;
        if (msg.eventNumber) this.eventNumber = msg.eventNumber;
        if (msg.eventName) this.eventName = msg.eventName;
        if (msg.heatNumber) this.heatNumber = msg.heatNumber;
        this.renderMeetInfo();
        break;
    }
  }

  applyStateSync(state) {
    if (state.meetName) this.meetName = state.meetName;
    if (state.eventNumber) this.eventNumber = state.eventNumber;
    if (state.eventName) this.eventName = state.eventName;
    if (state.heatNumber) this.heatNumber = state.heatNumber;
    this.renderMeetInfo();

    if (state.timingMode) {
      this.setTimingMode(state.timingMode);
    }

    if (state.status) {
      const s = state.status.toUpperCase();
      this.raceStatus = state.status;
      if (s === 'RUNNING') {
        this.isRunning = true;
        this.updateStatusTag('RUNNING', 'running');
      } else if (s === 'FINISHED') {
        this.isRunning = false;
        this.updateStatusTag('FINISHED', 'finished');
      } else {
        this.isRunning = false;
        this.updateStatusTag('READY', '');
      }
    }

    if (state.elapsedFormatted) {
      this.updateMasterDisplay(state.elapsedFormatted);
    }

    if (state.lanes && Array.isArray(state.lanes)) {
      this.lanes = state.lanes.map(l => ({
        laneNumber: l.laneNumber === 0 ? 10 : l.laneNumber,
        swimmerName: l.swimmerName || '',
        club: l.club || '',
        splitTime: l.splitTime || '',
        formattedTime: l.formattedTime || '00:00.00',
        rank: l.rank,
        status: l.status || 'Ready'
      }));
      this.lanes.sort((a, b) => a.laneNumber - b.laneNumber);
      this.renderPoolLanes();
    }

    if (state.owsRecords && Array.isArray(state.owsRecords)) {
      this.owsRecords = state.owsRecords;
      this.renderOwsView();
    }
  }

  setTimingMode(mode) {
    const isOws = mode === 'OpenWater' || mode === 'OPEN_WATER';
    this.timingMode = isOws ? 'OpenWater' : 'Pool';

    if (this.dom.badgeMode) {
      this.dom.badgeMode.textContent = isOws ? 'OPEN WATER (OWS)' : 'POOL SWIMMING';
      this.dom.badgeMode.style.background = isOws ? '#0F766E' : '#374151';
      this.dom.badgeMode.style.color = isOws ? '#5EEAD4' : '#E5E7EB';
    }

    if (isOws) {
      this.dom.poolView.style.display = 'none';
      this.dom.owsView.style.display = 'flex';
      this.renderOwsView();
    } else {
      this.dom.poolView.style.display = 'flex';
      this.dom.owsView.style.display = 'none';
      this.renderPoolLanes();
    }
  }

  renderMeetInfo() {
    if (this.dom.meetTitle) this.dom.meetTitle.textContent = (this.meetName || 'SWIMMING CHAMPIONSHIP').toUpperCase();
    if (this.dom.badgeEvent) this.dom.badgeEvent.textContent = 'EVENT #' + (this.eventNumber || 1);
    if (this.dom.eventName) this.dom.eventName.textContent = (this.eventName || 'FREESTYLE').toUpperCase();
    if (this.dom.badgeHeat) this.dom.badgeHeat.textContent = 'HEAT #' + (this.heatNumber || 1);
  }

  updateMasterDisplay(timeStr) {
    if (this.dom.masterTimer) {
      this.dom.masterTimer.textContent = timeStr || '00:00.00';
    }
  }

  updateStatusTag(text, className) {
    if (this.dom.raceStatusTag) {
      this.dom.raceStatusTag.textContent = text;
      this.dom.raceStatusTag.className = 'clock-status-tag ' + className;
    }
  }

  renderPoolLanes() {
    if (!this.dom.laneRowsContainer) return;
    const container = this.dom.laneRowsContainer;
    container.innerHTML = '';

    this.lanes.forEach((lane) => {
      const row = document.createElement('div');
      row.className = 'lane-row ' + lane.status.toLowerCase();
      row.id = 'laneRow-' + lane.laneNumber;

      let rankHtml = '<div class=rank-pill>-</div>';
      if (lane.status === 'Finished' && lane.rank) {
        const medalClass = lane.rank === 1 ? 'rank-1' : lane.rank === 2 ? 'rank-2' : lane.rank === 3 ? 'rank-3' : '';
        rankHtml = '<div class=rank-pill ' + medalClass + '>' + lane.rank + '</div>';
      } else if (['DQ', 'DNF', 'DNS'].includes(lane.status)) {
        rankHtml = '<div class=penalty-pill>' + lane.status + '</div>';
      }

      const swimmerDisplay = lane.swimmerName || '<span style=color: #475569; font-weight: normal;>Lane ' + lane.laneNumber + '</span>';

      row.innerHTML = 
        '<div class=col col-lane><div class=lane-badge>' + lane.laneNumber + '</div></div>' +
        '<div class=col col-swimmer>' + swimmerDisplay + '</div>' +
        '<div class=col col-club>' + (lane.club || '-') + '</div>' +
        '<div class=col col-split>' + (lane.splitTime || '-') + '</div>' +
        '<div class=col col-time>' + (lane.formattedTime || '00:00.00') + '</div>' +
        '<div class=col col-rank>' + rankHtml + '</div>';

      container.appendChild(row);
    });
  }

  renderOwsView() {
    if (!this.dom.owsRowsContainer) return;
    const container = this.dom.owsRowsContainer;
    container.innerHTML = '';

    const records = this.owsRecords || [];
    let finishedCount = 0;
    let penalizedCount = 0;

    records.forEach((rec, idx) => {
      const isFin = rec.status === 'Finished' || (!rec.status && rec.formattedTime);
      if (isFin) finishedCount++;
      if (['DQ', 'DNF', 'DNS'].includes(rec.status)) penalizedCount++;

      const row = document.createElement('div');
      row.className = 'ows-row ' + (isFin ? 'finished' : '');

      const medalClass = rec.rank === 1 ? 'rank-1' : rec.rank === 2 ? 'rank-2' : rec.rank === 3 ? 'rank-3' : '';
      const rankHtml = rec.rank ? '<div class=rank-pill ' + medalClass + '>' + rec.rank + '</div>' : '<div class=rank-pill>-</div>';
      const swimmerDisplay = rec.swimmerName || ('Athlete ' + (rec.bibNumber || (idx + 1)));

      row.innerHTML = 
        '<div class=col col-rank>' + rankHtml + '</div>' +
        '<div class=col col-bib><span class=bib-badge>' + (rec.bibNumber || '-') + '</span></div>' +
        '<div class=col col-swimmer>' + swimmerDisplay + '</div>' +
        '<div class=col col-club>' + (rec.club || '-') + '</div>' +
        '<div class=col col-gap>' + (rec.gap || (rec.rank === 1 ? 'LEADER' : '-')) + '</div>' +
        '<div class=col col-time>' + (rec.formattedTime || '00:00.00') + '</div>';

      container.appendChild(row);
    });

    if (this.dom.owsTotal) this.dom.owsTotal.textContent = records.length;
    if (this.dom.owsFinished) this.dom.owsFinished.textContent = finishedCount;
    if (this.dom.owsOnCourse) this.dom.owsOnCourse.textContent = Math.max(0, records.length - finishedCount - penalizedCount);
    if (this.dom.owsPenalized) this.dom.owsPenalized.textContent = penalizedCount;
  }

  handleLaneFinished(msg) {
    const laneNum = msg.laneNumber === 0 ? 10 : msg.laneNumber;
    const lane = this.lanes.find(l => l.laneNumber === laneNum);
    if (lane) {
      lane.status = 'Finished';
      lane.formattedTime = msg.formattedTime || msg.timeFormatted || lane.formattedTime;
      lane.rank = msg.rank;
      this.renderPoolLanes();
    }
  }

  handleLaneSplit(msg) {
    const laneNum = msg.laneNumber === 0 ? 10 : msg.laneNumber;
    const lane = this.lanes.find(l => l.laneNumber === laneNum);
    if (lane) {
      lane.splitTime = msg.splitTime || msg.formattedSplit;
      this.renderPoolLanes();
    }
  }

  handleLaneStatusChanged(msg) {
    const laneNum = msg.laneNumber === 0 ? 10 : msg.laneNumber;
    const lane = this.lanes.find(l => l.laneNumber === laneNum);
    if (lane) {
      lane.status = msg.status;
      this.renderPoolLanes();
    }
  }

  updateAllLanesStatus(status) {
    this.lanes.forEach(l => {
      if (l.status !== 'OFF') l.status = status;
    });
    this.renderPoolLanes();
  }

  resetLanesTimes() {
    this.lanes.forEach(l => {
      l.formattedTime = '00:00.00';
      l.splitTime = '';
      l.rank = null;
      l.status = 'Ready';
    });
    this.renderPoolLanes();
  }

  formatTime(ms) {
    if (!ms || ms < 0) return '00:00.00';
    const totalSeconds = Math.floor(ms / 1000);
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    const hundredths = Math.floor((ms % 1000) / 10);
    return String(minutes).padStart(2, '0') + ':' + String(seconds).padStart(2, '0') + '.' + String(hundredths).padStart(2, '0');
  }
}

document.addEventListener('DOMContentLoaded', () => {
  window.scoreboard = new ScoreboardController();
});