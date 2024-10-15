package main

import (
	"flag"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"

	"github.com/sirupsen/logrus"
	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/eventlog"
)

var (
	log         = logrus.New()
	startsetDir = "C:\\ProgramData\\Startset"
	serviceName = "StartsetService"
	interactive bool
	isDebug     = false // Set to true for debugging the service
)

// Command-line flags
var (
	bootEvery            = flag.Bool("boot-every", false, "Run scripts at every system boot (admin-level).")
	bootOnce             = flag.Bool("boot-once", false, "Run scripts once at system boot (admin-level).")
	loginWindow          = flag.Bool("login-window", false, "Run scripts at the login window (user-level).")
	loginPrivilegedEvery = flag.Bool("login-privileged-every", false, "Run privileged scripts every time a user logs in (admin-level).")
	loginPrivilegedOnce  = flag.Bool("login-privileged-once", false, "Run privileged scripts once at login (admin-level).")
	onDemand             = flag.Bool("on-demand", false, "Run scripts on demand (user-level).")
	loginEvery           = flag.Bool("login-every", false, "Run scripts every time a user logs in (user-level).")
	loginOnce            = flag.Bool("login-once", false, "Run scripts once at login (user-level).")
)

func init() {
	// Configure logging to a file
	log.Formatter = &logrus.TextFormatter{
		FullTimestamp: true,
	}
}

func main() {
	flag.Parse()

	var err error
	interactive, err = svc.IsAnInteractiveSession()
	if err != nil {
		log.Fatalf("Failed to determine if we are running in an interactive session: %v", err)
	}

	if interactive {
		// Running in interactive mode (console)
		setupLogging()
		ensureDirectories()
		log.Info("Startset application running interactively")
		processFlags()
	} else {
		// Running as a service
		runService(serviceName)
	}
}

// runService starts the service control dispatcher.
func runService(name string) {
	elog, err := eventlog.Open(name)
	if err != nil {
		return
	}
	defer elog.Close()

	elog.Info(1, fmt.Sprintf("%s service starting", name))
	err = svc.Run(name, &myService{elog: elog})
	if err != nil {
		elog.Error(1, fmt.Sprintf("%s service failed: %v", name, err))
		return
	}
	elog.Info(1, fmt.Sprintf("%s service stopped", name))
}

// myService implements the service interface
type myService struct {
	elog *eventlog.Log
}

func (m *myService) Execute(args []string, r <-chan svc.ChangeRequest, s chan<- svc.Status) (bool, uint32) {
	const cmdsAccepted = svc.AcceptStop | svc.AcceptShutdown
	s <- svc.Status{State: svc.StartPending}

	// Initialize service
	setupLogging()
	ensureDirectories()
	m.elog.Info(1, "Service initialized")

	s <- svc.Status{State: svc.Running, Accepts: cmdsAccepted}

	// Run the main logic in a separate goroutine
	go func() {
		// Define which scripts to run when the service starts
		// Adjust as needed
		runScripts(filepath.Join(startsetDir, "boot-every"), true)
		runScripts(filepath.Join(startsetDir, "boot-once"), true)
	}()

loop:
	for {
		select {
		case c := <-r:
			switch c.Cmd {
			case svc.Interrogate:
				s <- c.CurrentStatus
			case svc.Stop, svc.Shutdown:
				s <- svc.Status{State: svc.StopPending}
				// Perform any shutdown tasks here
				m.elog.Info(1, "Service is stopping")
				break loop
			default:
				m.elog.Warning(1, fmt.Sprintf("Received unexpected control request #%d", c))
			}
		}
	}

	s <- svc.Status{State: svc.Stopped}
	return false, 0
}

func setupLogging() {
	logFile := filepath.Join(startsetDir, "startset.log")
	file, err := os.OpenFile(logFile, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0666)
	if err != nil {
		log.Fatalf("Failed to open log file: %v", err)
	}
	log.Out = file
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

func processFlags() {
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

func runScripts(dir string, requireAdmin bool) {
	scripts, err := filepath.Glob(filepath.Join(dir, "*.ps1"))
	if err != nil {
		log.WithField("directory", dir).Error("Failed to list scripts: ", err)
		return
	}
	for _, script := range scripts {
		if requireAdmin && !isAdmin() {
			log.WithField("script", script).Info("Attempting to run script with admin privileges")
			if err := runWithAdminPrivileges(script); err != nil {
				log.WithField("script", script).Error("Failed to run script with admin privileges: ", err)
			}
		} else {
			log.WithField("script", script).Info("Running script")
			if err := exec.Command("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script).Run(); err != nil {
				log.WithField("script", script).Error("Failed to run script: ", err)
			}
		}
	}
}

func isAdmin() bool {
	var sid *windows.SID
	err := windows.AllocateAndInitializeSid(
		&windows.SECURITY_NT_AUTHORITY,
		2,
		windows.SECURITY_BUILTIN_DOMAIN_RID,
		windows.DOMAIN_ALIAS_RID_ADMINS,
		0, 0, 0, 0, 0, 0,
		&sid)
	if err != nil {
		log.Error("Failed to initialize SID: ", err)
		return false
	}
	defer windows.FreeSid(sid)

	token := windows.Token(0)
	member, err := token.IsMember(sid)
	if err != nil {
		log.Error("Failed to determine membership: ", err)
		return false
	}
	return member
}

func runWithAdminPrivileges(script string) error {
	cmd := exec.Command("powershell.exe", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script)
	cmd.SysProcAttr = &windows.SysProcAttr{
		CreationFlags: windows.CREATE_NEW_CONSOLE,
		Token:         0,
	}
	return cmd.Run()
}
