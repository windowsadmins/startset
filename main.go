package main

import (
	"fmt"
	"log"
	"os"
	"os/exec"
	"path/filepath"
	"flag"
	"golang.org/x/sys/windows"
)

var (
	startsetDir              = "C:\\ProgramData\\Startset"
	bootEveryDir             = filepath.Join(startsetDir, "boot-every")
	bootOnceDir              = filepath.Join(startsetDir, "boot-once")
	loginWindowDir           = filepath.Join(startsetDir, "login-window")
	loginPrivilegedEveryDir  = filepath.Join(startsetDir, "login-privileged-every")
	loginPrivilegedOnceDir   = filepath.Join(startsetDir, "login-privileged-once")
	loginEveryDir            = filepath.Join(startsetDir, "login-every")
	loginOnceDir             = filepath.Join(startsetDir, "login-once")
	onDemandDir              = filepath.Join(startsetDir, "on-demand")
	logDir                   = filepath.Join(startsetDir, "logs")
	logFile                  = filepath.Join(logDir, "startset.log")
)

var (
	bootEvery           = flag.Bool("boot-every", false, "Run scripts at every system boot (admin-level).")
	bootOnce            = flag.Bool("boot-once", false, "Run scripts once at system boot (admin-level).")
	loginWindow         = flag.Bool("login-window", false, "Run scripts at the login window (user-level).")
	loginPrivilegedEvery= flag.Bool("login-privileged-every", false, "Run privileged scripts every time a user logs in (admin-level).")
	loginPrivilegedOnce = flag.Bool("login-privileged-once", false, "Run privileged scripts once at login (admin-level).")
	onDemand            = flag.Bool("on-demand", false, "Run scripts on demand (user-level).")
	loginEvery          = flag.Bool("login-every", false, "Run scripts every time a user logs in (user-level).")
	loginOnce           = flag.Bool("login-once", false, "Run scripts once at login (user-level).")
	logFlag             = flag.Bool("log", false, "Display log content.")
)

func main() {
	flag.Parse()
	ensureDirectories()
	setupLogging()

	switch {
	case *bootEvery:
		runScripts(bootEveryDir, false, true) // Run every boot, admin-level
	case *bootOnce:
		runScripts(bootOnceDir, true, true) // Run once at boot, admin-level
	case *loginWindow:
		runScripts(loginWindowDir, false, false) // Run at login window, user-level
	case *loginPrivilegedEvery:
		runScripts(loginPrivilegedEveryDir, false, true) // Privileged scripts every login, admin-level
	case *loginPrivilegedOnce:
		runScripts(loginPrivilegedOnceDir, true, true) // Privileged scripts once per login, admin-level
	case *loginEvery:
		runScripts(loginEveryDir, false, false) // Run every login, user-level
	case *loginOnce:
		runScripts(loginOnceDir, true, false) // Run once per login, user-level
	case *onDemand:
		runScripts(onDemandDir, false, false) // Run on demand, user-level by default
	case *logFlag:
		displayLog()
	default:
		fmt.Println("Usage: startset --boot-every | --boot-once | --login-window | --login-privileged-every | --login-privileged-once | --on-demand | --login-every | --login-once | --log")
	}
}

func ensureDirectories() {
	directories := []string{bootEveryDir, bootOnceDir, loginWindowDir, loginPrivilegedEveryDir, loginPrivilegedOnceDir, loginEveryDir, loginOnceDir, onDemandDir, logDir}
	for _, dir := range directories {
		if err := os.MkdirAll(dir, os.ModePerm); err != nil {
			log.Fatalf("Failed to create directory %s: %v", dir, err)
		}
	}
}

func setupLogging() {
	logFile, err := os.OpenFile(logFile, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0644)
	if err != nil {
		log.Fatalf("Failed to open log file: %v", err)
	}
	log.SetOutput(logFile)
	log.Println("Startset initialized")
}

func runScripts(dir string, deleteAfterRun bool, requireAdmin bool) {
	files, err := filepath.Glob(filepath.Join(dir, "*.ps1"))
	if err != nil {
		log.Fatalf("Failed to list scripts in %s: %v", dir, err)
	}

	for _, script := range files {
		log.Printf("Running script: %s", script)
		if requireAdmin && !isAdmin() {
			err := runWithAdminPrivileges(script)
			if err != nil {
				log.Printf("Error running script %s with admin privileges: %v", script, err)
			}
		} else {
			err := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-File", script).Run()
			if err != nil {
				log.Printf("Error running script %s: %v", script, err)
			} else {
				log.Printf("Successfully ran script: %s", script)
				if deleteAfterRun {
					os.Remove(script)
				}
			}
		}
	}
}

func displayLog() {
	data, err := os.ReadFile(logFile)
	if err != nil {
		log.Fatalf("Failed to read log file: %v", err)
	}
	fmt.Println(string(data))
}

func isAdmin() bool {
	var sid *windows.SID
	var err error

	if sid, err = windows.CreateWellKnownSid(windows.WinBuiltinAdministratorsSid); err != nil {
		return false
	}
	token := windows.Token(0)
	isMember, err := token.IsMember(sid)
	if err != nil {
		log.Printf("Error checking admin status: %v", err)
	}
	return isMember
}

func runWithAdminPrivileges(script string) error {
	cmd := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-File", script)
	cmd.SysProcAttr = &windows.SysProcAttr{
		CreationFlags: windows.CREATE_NEW_CONSOLE | windows.CREATE_UNICODE_ENVIRONMENT,
	}
	return cmd.Run()
}
