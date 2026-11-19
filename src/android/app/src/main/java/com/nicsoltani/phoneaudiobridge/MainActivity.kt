package com.nicsoltani.phoneaudiobridge

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.view.ViewGroup.LayoutParams.MATCH_PARENT
import android.view.ViewGroup.LayoutParams.WRAP_CONTENT
import android.widget.Button
import android.widget.CheckBox
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.TextView

class MainActivity : Activity() {

    companion object {
        const val EXTRA_AUTOSTART = "autostart"
        private const val REQ_PERMISSIONS = 1
    }

    private var pendingAutostart = false
    private lateinit var statusView: TextView
    private lateinit var toggle: Button
    private val handler = Handler(Looper.getMainLooper())
    private val refresher = object : Runnable {
        override fun run() {
            refresh()
            handler.postDelayed(this, 500)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        buildUi()
        handleIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleIntent(intent)
    }

    override fun onResume() {
        super.onResume()
        handler.post(refresher)
        // Started here (not in onCreate) because Android only allows a microphone
        // foreground service to start while the activity is actually on screen.
        if (pendingAutostart) {
            pendingAutostart = false
            if (!AudioService.running) startBridge()
        }
    }

    override fun onPause() {
        handler.removeCallbacks(refresher)
        super.onPause()
    }

    private fun handleIntent(intent: Intent?) {
        // The Windows app launches us over adb with --ez autostart true.
        if (intent?.getBooleanExtra(EXTRA_AUTOSTART, false) == true) pendingAutostart = true
    }

    private fun dp(v: Int) = (v * resources.displayMetrics.density).toInt()

    private fun buildUi() {
        val prefs = getSharedPreferences(AudioService.PREFS, Context.MODE_PRIVATE)
        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(24), dp(24), dp(24), dp(24))
        }

        column.addView(TextView(this).apply {
            text = "Phone Audio Bridge"
            textSize = 24f
            setTypeface(typeface, Typeface.BOLD)
        })
        column.addView(TextView(this).apply {
            text = "Use this phone as the microphone and speaker of your Windows PC over USB."
            textSize = 15f
            setPadding(0, dp(6), 0, dp(20))
        })

        statusView = TextView(this).apply {
            textSize = 17f
            setPadding(0, 0, 0, dp(12))
        }
        column.addView(statusView)

        toggle = Button(this).apply {
            setOnClickListener {
                if (AudioService.running) stopBridge() else startBridge()
            }
        }
        column.addView(toggle, LinearLayout.LayoutParams(MATCH_PARENT, dp(56)))

        column.addView(TextView(this).apply {
            text = "Sound mode"
            textSize = 16f
            setTypeface(typeface, Typeface.BOLD)
            setPadding(0, dp(28), 0, dp(4))
        })
        val callId = 1001
        val musicId = 1002
        val group = RadioGroup(this)
        group.addView(RadioButton(this).apply {
            id = callId
            text = "Call mode - echo cancellation, best for meetings and calls"
        })
        group.addView(RadioButton(this).apply {
            id = musicId
            text = "Music mode - full-quality stereo, no echo cancellation"
        })
        group.check(if (prefs.getBoolean(AudioService.PREF_MUSIC_MODE, false)) musicId else callId)
        group.setOnCheckedChangeListener { _, checked ->
            prefs.edit().putBoolean(AudioService.PREF_MUSIC_MODE, checked == musicId).apply()
            if (AudioService.running) {
                // Drop the current session; the PC reconnects within a couple of seconds.
                startService(Intent(this, AudioService::class.java).setAction(AudioService.ACTION_RESTART_SESSION))
            }
        }
        column.addView(group)

        column.addView(CheckBox(this).apply {
            text = "Mute microphone"
            isChecked = AudioService.micMuted
            setPadding(0, dp(12), 0, dp(12))
            setOnCheckedChangeListener { _, checked -> AudioService.micMuted = checked }
        })

        column.addView(TextView(this).apply {
            text = "Setup: enable Developer options > USB debugging on this phone, plug it into the PC, " +
                "and run Phone Audio Bridge on Windows. The PC app starts this screen automatically.\n\n" +
                "Volume: use the phone's volume buttons. In call mode they adjust call volume."
            textSize = 13f
            setPadding(0, dp(16), 0, 0)
        })

        val scroll = ScrollView(this).apply {
            fitsSystemWindows = true
            addView(column, MATCH_PARENT, WRAP_CONTENT)
        }
        setContentView(scroll)
    }

    private fun refresh() {
        statusView.text = "Status: " + AudioService.status
        toggle.text = if (AudioService.running) "Stop" else "Start"
    }

    private fun startBridge() {
        val needed = ArrayList<String>()
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            needed.add(Manifest.permission.RECORD_AUDIO)
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            needed.add(Manifest.permission.POST_NOTIFICATIONS)
        }
        if (needed.isNotEmpty()) {
            requestPermissions(needed.toTypedArray(), REQ_PERMISSIONS)
        } else {
            launchService()
        }
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        // Start even if the microphone was refused: the speaker direction still works.
        if (requestCode == REQ_PERMISSIONS) launchService()
    }

    private fun launchService() {
        try {
            startForegroundService(Intent(this, AudioService::class.java))
        } catch (e: Exception) {
            AudioService.status = "Could not start: ${e.message}"
        }
        refresh()
    }

    private fun stopBridge() {
        stopService(Intent(this, AudioService::class.java))
        refresh()
    }
}
