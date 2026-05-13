package main

import (
	"embed"
	"html/template"
	"net/http"
)

//go:embed templates/dashboard.html
var dashboardFS embed.FS

var dashboardTemplate = template.Must(template.ParseFS(dashboardFS, "templates/dashboard.html"))

type dashboardView struct {
	Title string
}

func (s *server) dashboard(w http.ResponseWriter, r *http.Request) {
	if r.URL.Path != "/" {
		http.NotFound(w, r)
		return
	}

	w.Header().Set("Content-Type", "text/html; charset=utf-8")
	_ = dashboardTemplate.Execute(w, dashboardView{
		Title: "PFReport Logs",
	})
}
