package main

import (
    "os"
    "os/exec"
    "path/filepath"
    "flag"
    stdlog "log" // Use stdlog for the standard log package
    logrus "github.com/sirupsen/logrus" // Use logrus for the logrus package
    "golang.org/x/sys/windows"
)

var (
    log                  = logrus.New() // This initializes a logrus logger
    startsetDir          = "C:\\ProgramData\\Startset"
    bootEvery            = flag.Bool("boot-every", false, "Run scripts at every system boot (admin-level).")
    bootOnce             = flag.Bool("boot-once", false, "Run scripts once at system boot (admin-level).")
    loginWindow          = flag.Bool("login-window", false, "Run scripts at the login window (user-level).")
    loginPrivilegedEvery = flag.Bool("login-privileged-every", false, "Run privileged scripts every time a user logs in (admin-level).")
    loginPrivilegedOnce  = flag.Bool("login-privileged-once", false, "Run privileged scripts once at login (admin-level).")
    onDemand             = flag.Bool("on-demand", false, "Run scripts on demand (user-level).")
    loginEvery           = flag.Bool("login-every", false, "Run scripts every time a user logs in (user-level).")
    loginOnce            = flag.Bool("login-once", false, "Run scripts once at login (user-level).")
)

func main() {
	flag.Parse()
	setupLogging()

	ensureDirectories()

	log.Info("Startset application initialized")

	if *bootEvery {
		runScripts(filepath.Join(startsetDir, "boot-every"), true)
	}
	if *bootOnce {
		runScripts(filepath.Join(startsetDir, "boot-once"), true)
	}
	if *loginWindow {
		runScripts(filepath.Join(startsetDir, "login-window"), false)
	}
	if *loginPrivilegedEvery {
		runScripts(filepath.Join(startsetDir, "login-privileged-every"), true)
	}
	if *loginPrivilegedOnce {
		runScripts(filepath.Join(startsetDir, "login-privileged-once"), true)
	}
	if *onDemand {
		runScripts(filepath.Join(startsetDir, "on-demand"), false)
	}
	if *loginEvery {
		runScripts(filepath.Join(startsetDir, "login-every"), false)
	}
	if *loginOnce {
		runScripts(filepath.Join(startsetDir, "login-once"), false)
	}
}

func setupLogging() {
    file, err := os.OpenFile(filepath.Join(startsetDir, "startset.log"), os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0666)
    if err != nil {
        log.Fatalf("Failed to open log file: %v", err) // Using logrus for fatal logging
    }
    log.Out = file
    log.Formatter = &logrus.JSONFormatter{}
}

func ensureDirectories() {
	directories := []string{
		filepath.Join(startsetDir, "boot-every"),
		filepath.Join(startsetDir, "boot-once"),
		filepath.Join(startsetDir, "login-window"),
		filepath.Join(startsetDir, "login-privileged-every"),
		filepath.Join(startsetDir, "login-privileged-once"),
		filepath.Join(startsetDir, "on-demand"),
		filepath.Join(startsetDir, "login-every"),
		filepath.Join(startsetDir, "login-once"),
	}
	for _, dir := range directories {
		if err := os.MkdirAll(dir, 0755); err != nil {
			log.WithField("directory", dir).Error("Failed to create directory: ", err)
		}
	}
}

func runScripts(dir string, requireAdmin bool) {
	files, err := filepath.Glob(filepath.Join(dir, "*.ps1"))
	if err != nil {
		log.WithField("directory", dir).Error("Failed to list scripts: ", err)
		return
	}
	for _, script := range files {
		if requireAdmin && !isAdmin() {
			log.WithField("script", script).Info("Running script with admin privileges")
			if err := runWithAdminPrivileges(script); err != nil {
				log.WithField("script", script).Error("Failed to run script with admin privileges: ", err)
			}
		} else {
			log.WithField("script", script).Info("Running script")
			if err := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-File", script).Run(); err != nil {
				log.WithField("script", script).Error("Failed to run script: ", err)
			}
		}
	}
}

func isAdmin() bool {
	var sid *windows.SID
	// Check if the current user is an admin
	if err := windows.AllocateAndInitializeSid(&windows.SECURITY_NT_AUTHORITY, 2, windows.SECURITY_BUILTIN_DOMAIN_RID, windows.DOMAIN_ALIAS_RID_ADMINS, 0, 0, 0, 0, 0, 0, &sid); err != nil {
		log.Error("Failed to initialize SID: ", err)
		return false
	}
	defer windows.FreeSid(sid)
	token := windows.Token(0)
	isAdmin, err := token.IsMember(sid)
	if err != nil {
		log.Error("Failed to determine membership: ", err)
		return false
	}
	return isAdmin
}

func runWithAdminPrivileges(script string) error {
	cmd := exec.Command("powershell.exe", "-ExecutionPolicy", "Bypass", "-File", script)
	cmd.SysProcAttr = &windows.SysProcAttr{CreationFlags: windows.CREATE_NEW_CONSOLE}
	return cmd.Run()
}
