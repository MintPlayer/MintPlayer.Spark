import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';

@Component({
  selector: 'app-home',
  standalone: true,
  imports: [CommonModule],
  template: `
    <div class="container">
      <div class="row justify-content-center">
        <div class="col-md-8">
          <div class="card">
            <div class="card-body text-center">
              <h1 class="display-4">Welcome to Spark</h1>
              <p class="lead">A low-code framework for building data-driven web applications.</p>
              <hr>
              <p>Select a menu item from the sidebar to get started.</p>
            </div>
          </div>
        </div>
      </div>
    </div>
  `
})
export default class HomeComponent {}
